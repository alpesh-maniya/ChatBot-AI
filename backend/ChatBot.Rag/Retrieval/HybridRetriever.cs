using System.Diagnostics;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Index;
using ChatBot.Rag.Ingestion;

namespace ChatBot.Rag.Retrieval;

public sealed record RetrievedSource(
    int Number,
    DocumentChunk Chunk,
    float? VectorScore,
    int? VectorRank,
    int? Bm25Rank,
    double FusedScore);

/// <param name="Grounded">False when the best vector score is under the threshold: the
/// documents most likely don't cover the question, and the LLM should not be asked.</param>
/// <param name="Sections">Sections the question named, when search was limited to them.</param>
public sealed record RetrievalResult(
    SearchQuery Query,
    IReadOnlyList<RetrievedSource> Sources,
    float TopVectorScore,
    bool Grounded,
    double ElapsedMilliseconds,
    IReadOnlyList<string>? Sections = null)
{
    /// <summary>Each document's best cosine, and whether it was close enough to the best to be searched.</summary>
    public IReadOnlyList<(string DocId, float Best, bool Searched)> DocumentScores { get; init; } = [];
}

// One document's loaded index plus the two search structures built over it.
public sealed class RagIndex
{
    private readonly Dictionary<string, DocumentChunk> _byId;

    private RagIndex(LoadedIndex loaded, DocumentManifest manifest)
    {
        Manifest = manifest;
        Chunks = loaded.Chunks;
        Acronyms = loaded.Acronyms;
        Vectors = new FlatVectorIndex(loaded.Vectors, manifest.EmbeddingDimensions);
        Keywords = new Bm25Index(loaded.Chunks.Select(c => c.IndexText).ToArray());
        Router = new SectionRouter(loaded.Chunks, manifest.Routes);
        _byId = loaded.Chunks.ToDictionary(c => c.Id);
    }

    public DocumentManifest Manifest { get; }
    public IReadOnlyList<DocumentChunk> Chunks { get; }
    public IReadOnlyDictionary<string, string> Acronyms { get; }
    public FlatVectorIndex Vectors { get; }
    public Bm25Index Keywords { get; }
    public SectionRouter Router { get; }

    public DocumentChunk? Find(string chunkId) => _byId.GetValueOrDefault(chunkId);

    /// <summary>
    /// The first text chunk of the run of chunks under the same heading as
    /// <paramref name="chunk"/>. Tables inside the section are part of the run but are never
    /// its opening. Chunk ordinals are their rows.
    /// </summary>
    public DocumentChunk SectionStart(DocumentChunk chunk)
    {
        var row = chunk.Ordinal;
        while (row > 0 && Chunks[row - 1].SectionPath.SequenceEqual(chunk.SectionPath)) row--;

        for (; row <= chunk.Ordinal; row++)
        {
            if (Chunks[row].Kind == ChunkKind.Text) return Chunks[row];
        }
        return chunk;
    }

    /// <param name="profile">When given, its title, subject and suggestions replace the
    /// manifest's. They are display text, not index content.</param>
    public static RagIndex Load(string folder, IngestProfile? profile = null)
    {
        var loaded = IndexStore.Read(folder);
        var manifest = profile is null
            ? loaded.Manifest
            : loaded.Manifest with
            {
                Title = profile.Title,
                Subject = profile.Subject ?? loaded.Manifest.Subject,
                ExampleQuestions = profile.ExampleQuestions.Count > 0 ? profile.ExampleQuestions : loaded.Manifest.ExampleQuestions,
                Routes = profile.Routes,
                SectionLead = profile.SectionLead
            };
        return new RagIndex(loaded, manifest);
    }
}

//   question --+--> embed --> cosine top 20 --+
//              |                              +--> RRF --> top 4 within budget --> gate
//              +--> expand --> BM25 top 20 ---+
//
// Over several documents each index is searched on its own and the lists merged. Cosine
// scores share one scale (same model, normalised vectors) so the vector lists merge by
// score. BM25 scores don't - each index has its own IDF - so those merge by per-document
// rank. With one document both merges are the identity, which keeps single-document
// results exactly as they were.
public sealed class HybridRetriever
{
    private readonly IReadOnlyList<RagIndex> _indexes;
    private readonly IEmbeddingModel _embedder;
    private readonly RetrievalOptions _options;
    private readonly QueryBuilder _queries;
    private readonly int[] _offsets;

    public HybridRetriever(RagIndex index, IEmbeddingModel embedder, RetrievalOptions options)
        : this([index], embedder, options)
    {
    }

    public HybridRetriever(IReadOnlyList<RagIndex> indexes, IEmbeddingModel embedder, RetrievalOptions options)
    {
        if (indexes.Count == 0) throw new ArgumentException("At least one index is needed.", nameof(indexes));

        _indexes = indexes;
        _embedder = embedder;
        _options = options;
        _queries = new QueryBuilder(indexes.Count == 1 ? indexes[0].Acronyms : MergeAcronyms(indexes));

        // Global row = offset of its index + row inside it, so RRF can work on plain ints.
        _offsets = new int[indexes.Count];
        for (var i = 1; i < indexes.Count; i++) _offsets[i] = _offsets[i - 1] + indexes[i - 1].Chunks.Count;
    }

    public IReadOnlyList<RagIndex> Indexes => _indexes;

    public async Task<RetrievalResult> RetrieveAsync(
        string message,
        string? previousUserMessage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = _queries.Build(message, previousUserMessage);

        var queryVector = await _embedder.EmbedQueryAsync(query.Vector, cancellationToken);

        // Routed on this message alone: a follow-up that names no section searches everywhere.
        var filters = _indexes.Select(index => index.Router.Route(message)).ToArray();

        var perIndex = _indexes
            .Select((index, i) => index.Vectors.Search(queryVector, _options.VectorTopK, filters[i]?.Rows)
                .Select(h => (Row: _offsets[i] + h.Row, h.Score))
                .ToArray())
            .ToArray();
        var best = perIndex.Select(hits => hits.Length > 0 ? hits[0].Score : 0f).ToArray();

        // Which reports the question is about. BM25 always finds something, so with three
        // reports a GPO question also gets the World Bank's best keyword matches for
        // "employ", and those take two thirds of the keyword slots. A report whose best
        // passage is well below the best report's sits the question out, and the rest is
        // searched exactly as it would be alone. "Compare A with B" keeps both.
        var topScore = best.Length > 0 ? best.Max() : 0f;
        var inPlay = best.Select(b => _options.DocumentMargin <= 0 || b >= topScore - _options.DocumentMargin).ToArray();

        var vectorHits = perIndex
            .Where((_, i) => inPlay[i])
            .SelectMany(hits => hits)
            .OrderByDescending(h => h.Score)
            .Take(_options.VectorTopK)
            .ToArray();

        var keywordHits = _indexes
            .SelectMany((index, i) => !inPlay[i] ? [] : index.Keywords.Search(query.Keyword, _options.Bm25TopK, filters[i]?.Rows)
                .Select((h, rank) => (Row: _offsets[i] + h.Row, Rank: rank, h.Score)))
            .OrderBy(h => h.Rank)
            .ThenByDescending(h => h.Score)
            .Take(_options.Bm25TopK)
            .ToArray();

        var fused = RrfFusion.Fuse(
            [vectorHits.Select(h => h.Row).ToArray(), keywordHits.Select(h => h.Row).ToArray()],
            _options.RrfK);

        var vectorRank = vectorHits.Select((h, i) => (h.Row, h.Score, Rank: i + 1)).ToDictionary(x => x.Row);
        var keywordRank = keywordHits.Select((h, i) => (h.Row, Rank: i + 1)).ToDictionary(x => x.Row, x => x.Rank);

        var sources = new List<RetrievedSource>();
        var spent = 0;

        foreach (var (row, score) in fused)
        {
            if (sources.Count >= _options.MaxSources) break;

            var chunk = ChunkAt(row);
            var cost = PromptCost(chunk, sources.Count + 1);
            if (sources.Count > 0 && spent + cost > _options.SourceTokenBudget) continue;

            spent += cost;
            var v = vectorRank.TryGetValue(row, out var vr) ? vr : default;
            sources.Add(new RetrievedSource(
                Number: sources.Count + 1,
                Chunk: chunk,
                VectorScore: v.Rank > 0 ? v.Score : null,
                VectorRank: v.Rank > 0 ? v.Rank : null,
                Bm25Rank: keywordRank.TryGetValue(row, out var kr) ? kr : null,
                FusedScore: score));
        }

        sources = OpenWithSectionStart(sources);

        var top = vectorHits.Length > 0 ? vectorHits[0].Score : 0f;
        stopwatch.Stop();

        var sections = filters.Where(f => f is not null).SelectMany(f => f!.Sections).Distinct().ToArray();
        return new RetrievalResult(query, sources, top, top >= _options.MinVectorScore, stopwatch.Elapsed.TotalMilliseconds,
            sections.Length > 0 ? sections : null)
        {
            DocumentScores = _indexes.Select((index, i) => (index.Manifest.DocId, best[i], inPlay[i])).ToArray()
        };
    }

    // Profile option SectionLead. Only when the best match has company: one hit in a section
    // says little about the section, two say the question is about it. The section start
    // takes slot [1], which is the one passage sent whole; the last source makes room.
    private List<RetrievedSource> OpenWithSectionStart(List<RetrievedSource> sources)
    {
        if (sources.Count < 2) return sources;

        var best = sources[0].Chunk;
        var index = _indexes.First(i => i.Manifest.DocId == best.DocId);
        if (!index.Manifest.SectionLead) return sources;

        var company = sources.Skip(1).Any(s => s.Chunk.DocId == best.DocId && s.Chunk.SectionPath.SequenceEqual(best.SectionPath));
        if (!company) return sources;

        var start = index.SectionStart(best);
        if (start.Id == best.Id) return sources;

        var reordered = new List<RetrievedSource>
        {
            sources.FirstOrDefault(s => s.Chunk.Id == start.Id) ?? new RetrievedSource(0, start, null, null, null, 0)
        };
        reordered.AddRange(sources.Where(s => s.Chunk.Id != start.Id));

        return reordered.Take(_options.MaxSources).Select((s, i) => s with { Number = i + 1 }).ToList();
    }

    // What the passage will cost in the prompt, not on disk. Passages after the first
    // WholePassages are cut to PassageMaxChars before they are sent; charging them in full
    // dropped South Asia's "6.3 percent in 2026" chunk for two forecast tables that were
    // then trimmed to a few rows anyway. ~4 characters a token.
    private int PromptCost(DocumentChunk chunk, int number) =>
        _options.PassageMaxChars <= 0 || number <= _options.WholePassages
            ? chunk.TokenCount
            : Math.Min(chunk.TokenCount, _options.PassageMaxChars / 4);

    private DocumentChunk ChunkAt(int globalRow)
    {
        var i = _offsets.Length - 1;
        while (_offsets[i] > globalRow) i--;
        return _indexes[i].Chunks[globalRow - _offsets[i]];
    }

    // First document wins on a clash; acronyms only widen the keyword query, so a wrong
    // expansion costs a little ranking noise, not a wrong answer.
    private static Dictionary<string, string> MergeAcronyms(IEnumerable<RagIndex> indexes)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        foreach (var (acronym, expansion) in index.Acronyms)
        {
            merged.TryAdd(acronym, expansion);
        }
        return merged;
    }
}
