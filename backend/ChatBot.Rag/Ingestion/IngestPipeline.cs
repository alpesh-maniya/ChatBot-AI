using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChatBot.Rag.Chunking;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Index;

namespace ChatBot.Rag.Ingestion;

public enum IngestStage
{
    Reading,
    Classifying,
    Chunking,
    Embedding,
    Writing
}

public sealed record IngestProgress(IngestStage Stage, string Message, int Done = 0, int Total = 0);

public sealed record IngestResult(
    DocumentManifest Manifest,
    AssembledDocument Document,
    IReadOnlyList<DocumentChunk> Chunks,
    int PagesRead,
    TimeSpan Elapsed);

// PDF -> index folder, the five steps ChatBot.Ingest used to run inline. The console tool
// and the API's upload worker both call this, so a document indexed either way is the
// same bytes on disk.
public sealed partial class IngestPipeline(IEmbeddingModel embedder, ChunkingOptions? chunking = null)
{
    private readonly ChunkingOptions _chunking = chunking ?? new ChunkingOptions();

    public async Task<IngestResult> RunAsync(
        string pdfPath,
        IngestProfile profile,
        string outputFolder,
        IProgress<IngestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var reader = new PdfLayoutReader();
        var pageCount = reader.CountPages(pdfPath);

        progress?.Report(new IngestProgress(IngestStage.Reading, $"Reading {pageCount} pages"));
        var pages = await Task.Run(() => reader.Read(pdfPath, profile.SkipPages.ToHashSet()), cancellationToken);

        progress?.Report(new IngestProgress(IngestStage.Classifying, "Finding headings, tables and captions"));
        var document = await Task.Run(() => new DocumentAssembler(profile).Assemble(pages), cancellationToken);
        if (document.Blocks.Count(b => b.Kind != BlockKind.Heading) == 0)
        {
            throw new InvalidDataException("No text found in the PDF. Scanned (image-only) documents need OCR first.");
        }

        progress?.Report(new IngestProgress(IngestStage.Chunking, "Splitting into passages"));
        var chunks = new SectionChunker(_chunking, embedder).Chunk(document, profile);
        if (chunks.Count == 0)
        {
            throw new InvalidDataException("The PDF has text but nothing long enough to index.");
        }

        var tooLong = chunks.Where(c => c.TokenCount > embedder.ContextLimit).ToList();
        if (tooLong.Count > 0)
        {
            throw new InvalidDataException(
                $"{tooLong.Count} passage(s) exceed the embedder's {embedder.ContextLimit} tokens: " +
                string.Join("; ", tooLong.Take(5).Select(c => $"{c.Id} ({c.Kind}, p. {c.PdfPageStart}, {c.TokenCount} tokens, header \"{Shorten(c.Header, 300)}\": \"{Shorten(c.Text, 120)}\")")));
        }

        var vectors = new List<float[]>(chunks.Count);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(await embedder.EmbedDocumentAsync(chunk.IndexText, cancellationToken));

            if (vectors.Count % 10 == 0 || vectors.Count == chunks.Count)
            {
                progress?.Report(new IngestProgress(IngestStage.Embedding, $"Embedding {vectors.Count}/{chunks.Count}", vectors.Count, chunks.Count));
            }
        }

        progress?.Report(new IngestProgress(IngestStage.Writing, "Writing the index"));
        var manifest = new DocumentManifest
        {
            DocId = profile.DocId,
            Title = profile.Title,
            SourceFile = Path.GetFileName(pdfPath),
            SourceSha256 = await Sha256Async(pdfPath, cancellationToken),
            PdfPageCount = pageCount,
            CreatedAt = DateTimeOffset.UtcNow,
            EmbeddingModel = embedder.Name,
            EmbeddingDimensions = embedder.Dimensions,
            ChunkerVersion = DocumentManifest.CurrentChunkerVersion,
            Chunking = _chunking,
            ChunkCount = chunks.Count,
            Parts = profile.Parts.Select(p => new PartInfo(p.Name, p.Label, p.FromPage, p.ToPage)).ToArray(),
            Subject = profile.Subject,
            ExampleQuestions = profile.ExampleQuestions.Count > 0 ? profile.ExampleQuestions : SuggestQuestions(chunks),
            Routes = profile.Routes,
            SectionLead = profile.SectionLead
        };

        // Written to a sibling folder and swapped in, so a crash halfway never leaves a
        // folder that looks like an index but has 40 vectors for 150 chunks.
        var staging = outputFolder.TrimEnd('/', '\\') + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        IndexStore.Write(staging, manifest, chunks, vectors, document.Acronyms);
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, recursive: true);
        Directory.Move(staging, outputFolder);

        return new IngestResult(manifest, document, chunks, pages.Count, total.Elapsed);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    /// <summary>
    /// Profile for a PDF that has none: id from the file name plus a hash prefix (so two
    /// "report.pdf" uploads don't collide), title from metadata or the file name.
    /// </summary>
    public static IngestProfile DefaultProfile(string pdfPath, string sha256)
    {
        var reader = new PdfLayoutReader();
        var title = GuessTitle(reader.MetadataTitle(pdfPath), Path.GetFileNameWithoutExtension(pdfPath));
        return IngestProfile.CreateDefault(DocIdFor(pdfPath, sha256), title, reader.CountPages(pdfPath));
    }

    public static string DocIdFor(string pdfPath, string sha256)
    {
        var slug = NonSlug().Replace(Path.GetFileNameWithoutExtension(pdfPath).ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        if (slug.Length == 0) slug = "document";
        return $"{slug}-{sha256[..8]}";
    }

    internal static string GuessTitle(string? metadataTitle, string fileName)
    {
        var meta = metadataTitle?.Trim() ?? "";
        meta = OfficePrefix().Replace(meta, "");
        meta = Path.HasExtension(meta) && meta.Length - Path.GetExtension(meta).Length > 0 && Path.GetExtension(meta).Length <= 5
            ? Path.GetFileNameWithoutExtension(meta)
            : meta;

        if (meta.Length is >= 4 and <= 120 && meta.Any(char.IsLetter)
            && !meta.Equals("untitled", StringComparison.OrdinalIgnoreCase) && !LooksLikeId(meta))
        {
            return meta;
        }

        var name = Separators().Replace(fileName, " ").Trim();
        name = Spaces().Replace(name, " ");
        return name.Length == 0 || LooksLikeId(name) ? "Uploaded document" : name;
    }

    // "1DZT7OB2B3ZBJP08FDX65B0C7LNW5AXGRUJYCGIN8FS3C8PCUN" is a storage key, not a name.
    private static bool LooksLikeId(string text) =>
        text.Split(' ').Any(w => w.Length >= 20 && w.Count(char.IsDigit) >= 4);

    private static string Shorten(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" | ");
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    // "What does the document say about capital contributions?" for the three sections
    // with the most text, in document order. Cover lines and one-word headings fail the
    // filter; a document nobody wrote suggestions for still gets something sensible.
    internal static IReadOnlyList<string> SuggestQuestions(IReadOnlyList<DocumentChunk> chunks) =>
        chunks
            .Where(c => c.SectionPath.Count > 0)
            .GroupBy(c => c.SectionPath[0])
            .Select(g => (Name: SectionName(g.Key), First: g.Min(c => c.Ordinal), Tokens: g.Sum(c => c.TokenCount)))
            .Where(s => s.Name is not null)
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Tokens)
            .Take(3)
            .OrderBy(s => s.First)
            .Select(s => $"What does the document say about {s.Name}?")
            .ToArray();

    private static string? SectionName(string heading)
    {
        var text = LeadingNumber().Replace(heading.Trim(), "").Trim().TrimEnd('.', ':');
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 8 || text.StartsWith('[') || text.StartsWith('(')) return null;
        if (text.Count(char.IsLetter) < text.Length * 0.7) return null;
        if (text.Contains("contents", StringComparison.OrdinalIgnoreCase)) return null;

        // "CAPITAL CONTRIBUTIONS" reads as shouting in a suggestion.
        return text.Any(char.IsLower) ? text : text.ToLowerInvariant();
    }

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3})*\.?|[A-Z]\.|[IVX]+\.)\s+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^(Microsoft (Word|PowerPoint|Excel) - )", RegexOptions.IgnoreCase)]
    private static partial Regex OfficePrefix();

    [GeneratedRegex(@"[_\-.]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
