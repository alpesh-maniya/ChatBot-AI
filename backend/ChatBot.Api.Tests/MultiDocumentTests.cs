using ChatBot.Api.Rag;
using ChatBot.Rag.Chunking;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Index;
using ChatBot.Rag.Ingestion;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Tests;

// Returns the same query vector for everything, so a test decides the cosine scores by
// choosing the stored vectors.
internal sealed class FixedEmbedder(float[] query) : IEmbeddingModel
{
    public string Name => "fake.gguf";
    public int Dimensions => query.Length;
    public bool IsReady => true;
    public int ContextLimit => 512;
    public int CountTokens(string text) => text.Split(' ').Length;
    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken) => Task.FromResult(query);
    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => Task.FromResult(query);
}

public sealed class MultiDocumentRetrievalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chatbot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private RagIndex Index(string docId, params (string Text, float[] Vector)[] rows)
    {
        var chunks = rows.Select((r, i) => new DocumentChunk
        {
            Id = $"{docId}:{i:D4}", DocId = docId, Ordinal = i, Kind = ChunkKind.Text, Part = "Document", PartLabel = "",
            SectionPath = [], PdfPageStart = i + 1, PdfPageEnd = i + 1, Header = docId, Text = r.Text,
            TokenCount = 10, ContentHash = i.ToString()
        }).ToArray();

        var manifest = new DocumentManifest
        {
            DocId = docId, Title = docId, SourceFile = docId + ".pdf", SourceSha256 = "0", PdfPageCount = rows.Length,
            CreatedAt = DateTimeOffset.UtcNow, EmbeddingModel = "fake.gguf", EmbeddingDimensions = 2,
            ChunkerVersion = DocumentManifest.CurrentChunkerVersion, Chunking = new ChunkingOptions(), ChunkCount = rows.Length
        };

        var folder = Path.Combine(_root, docId);
        IndexStore.Write(folder, manifest, chunks, rows.Select(r => r.Vector).ToArray(), new Dictionary<string, string>());
        return RagIndex.Load(folder);
    }

    [Fact]
    public async Task Merges_documents_by_cosine_and_maps_rows_back_to_the_right_document()
    {
        var a = Index("a", ("revenue grew in the year", [1f, 0f]), ("unrelated staffing note", [0f, 1f]));
        var b = Index("b", ("revenue fell sharply", [0.8f, 0.6f]), ("a different topic entirely", [-1f, 0f]));

        var retriever = new HybridRetriever([a, b], new FixedEmbedder([1f, 0f]),
            new RetrievalOptions { MaxSources = 2, SourceTokenBudget = 1000, MinVectorScore = 0.5f });
        var result = await retriever.RetrieveAsync("revenue", null, CancellationToken.None);

        Assert.True(result.Grounded);
        Assert.Equal(1f, result.TopVectorScore, 3);
        Assert.Equal(["a:0000", "b:0000"], result.Sources.Select(s => s.Chunk.Id));
        Assert.Equal(0.8f, result.Sources[1].VectorScore!.Value, 3);
    }

    [Fact]
    public async Task One_document_in_a_list_ranks_the_same_as_the_single_index_constructor()
    {
        var a = Index("a", ("revenue grew", [0.6f, 0.8f]), ("revenue and income", [1f, 0f]), ("staff numbers", [0f, 1f]));
        var options = new RetrievalOptions { MaxSources = 3, SourceTokenBudget = 1000 };
        var embedder = new FixedEmbedder([1f, 0f]);

        var single = await new HybridRetriever(a, embedder, options).RetrieveAsync("revenue", null, CancellationToken.None);
        var list = await new HybridRetriever([a], embedder, options).RetrieveAsync("revenue", null, CancellationToken.None);

        Assert.Equal(single.Sources.Select(s => (s.Chunk.Id, s.FusedScore)), list.Sources.Select(s => (s.Chunk.Id, s.FusedScore)));
    }

    // "revenue" is a keyword match in b too, so without the margin b's passage gets in on
    // BM25 alone, though its cosine (0.6) is far below a's best (1.0).
    [Fact]
    public async Task A_document_far_below_the_best_one_sits_the_question_out()
    {
        var a = Index("a", ("revenue grew in the year", [1f, 0f]), ("revenue by business line", [0.9f, 0.44f]), ("staffing", [0f, 1f]));
        var b = Index("b", ("revenue forecast for the region", [0.6f, 0.8f]), ("growth outlook", [0f, 1f]));
        var embedder = new FixedEmbedder([1f, 0f]);
        var options = new RetrievalOptions { MaxSources = 3, SourceTokenBudget = 1000 };

        var all = await new HybridRetriever([a, b], embedder, options).RetrieveAsync("revenue", null, CancellationToken.None);
        var gated = await new HybridRetriever([a, b], embedder, options with { DocumentMargin = 0.05f }).RetrieveAsync("revenue", null, CancellationToken.None);
        var alone = await new HybridRetriever(a, embedder, options).RetrieveAsync("revenue", null, CancellationToken.None);

        Assert.Contains(all.Sources, s => s.Chunk.DocId == "b");
        Assert.DoesNotContain(gated.Sources, s => s.Chunk.DocId == "b");
        Assert.Equal(alone.Sources.Select(s => s.Chunk.Id), gated.Sources.Select(s => s.Chunk.Id));
        Assert.Equal([("a", true), ("b", false)], gated.DocumentScores.Select(d => (d.DocId, d.Searched)));
    }

    [Fact]
    public async Task Documents_close_to_the_best_one_are_all_searched()
    {
        var a = Index("a", ("revenue grew", [1f, 0f]));
        var b = Index("b", ("revenue fell", [0.98f, 0.2f]));

        var result = await new HybridRetriever([a, b], new FixedEmbedder([1f, 0f]),
            new RetrievalOptions { MaxSources = 2, SourceTokenBudget = 1000, DocumentMargin = 0.05f }).RetrieveAsync("revenue", null, CancellationToken.None);

        Assert.Equal(["a", "b"], result.Sources.Select(s => s.Chunk.DocId));
    }
}

public sealed class PromptTemplateTests
{
    private static readonly RagOptions Options = new()
    {
        FallbackMessage = "I couldn't find that in {documents}.",
        AnswerInstruction = "Answer only from the Sources above. If the Sources do not answer the question, reply: {fallback}",
        SystemPrompt = "You answer questions about {subject}.\n\n5. If the Sources do not answer the question, reply exactly: \"{fallback}\""
    };

    [Fact]
    public void One_document_with_a_subject_fills_in_exactly_the_old_GPO_prompt()
    {
        var (system, instruction, fallback) = RagRuntime.FillPrompts(Options,
            [("GPO 2018 Annual Report", "the U.S. Government Publishing Office (GPO) 2018 Annual Report")]);

        Assert.Equal("I couldn't find that in the GPO 2018 Annual Report.", fallback);
        Assert.Equal(
            "You answer questions about the U.S. Government Publishing Office (GPO) 2018 Annual Report.\n\n" +
            "5. If the Sources do not answer the question, reply exactly: \"I couldn't find that in the GPO 2018 Annual Report.\"",
            system);
        Assert.EndsWith("reply: I couldn't find that in the GPO 2018 Annual Report.", instruction);
    }

    [Fact]
    public void Several_documents_are_listed_and_the_fallback_names_none_of_them()
    {
        var (system, _, fallback) = RagRuntime.FillPrompts(Options, [("Northstar Q4 Report", null), ("The Meridian PPM", null)]);

        Assert.StartsWith("You answer questions about these documents: the Northstar Q4 Report; The Meridian PPM.", system);
        Assert.Equal("I couldn't find that in the selected documents.", fallback);
    }
}

public sealed class DefaultProfileTests
{
    [Fact]
    public void Doc_id_is_a_slug_of_the_file_name_plus_a_hash_prefix()
    {
        Assert.Equal("northstar-health-q4-report-6cf0d991",
            IngestPipeline.DocIdFor(@"C:\uploads\Northstar Health_Q4 Report.pdf", "6cf0d9911f2e"));
    }

    [Theory]
    [InlineData("Microsoft Word - Fund Memo.docx", "anything", "Fund Memo")]
    [InlineData(null, "Northstar_Health_Systems_2025_Q4", "Northstar Health Systems 2025 Q4")]
    [InlineData("untitled", "Meridian-Growth-Fund-IV-PPM", "Meridian Growth Fund IV PPM")]
    [InlineData(null, "1DZT7OB2B3ZBJP08FDX65B0C7LNW5AXGRUJYCGIN8FS3C8PCUN", "Uploaded document")]
    public void Title_prefers_real_metadata_then_the_file_name(string? metadata, string fileName, string expected)
    {
        Assert.Equal(expected, IngestPipeline.GuessTitle(metadata, fileName));
    }

    [Fact]
    public void Suggests_questions_for_the_biggest_named_sections_in_document_order()
    {
        DocumentChunk Chunk(int i, string section, int tokens) => new()
        {
            Id = $"d:{i:D4}", DocId = "d", Ordinal = i, Kind = ChunkKind.Text, Part = "Document", PartLabel = "",
            SectionPath = [section], PdfPageStart = 1, PdfPageEnd = 1, Header = "", Text = "", TokenCount = tokens, ContentHash = ""
        };

        var questions = IngestPipeline.SuggestQuestions(
        [
            Chunk(0, "[July 2020]", 900),
            Chunk(1, "2. GENERAL PROVISIONS", 300),
            Chunk(2, "Fund Overview", 500),
            Chunk(3, "Summary", 50),
            Chunk(4, "6. CAPITAL CONTRIBUTIONS", 400),
            Chunk(5, "Risk Factors", 100)
        ]);

        Assert.Equal(
        [
            "What does the document say about general provisions?",
            "What does the document say about Fund Overview?",
            "What does the document say about capital contributions?"
        ], questions);
    }
}

public sealed class NumberedClauseTests
{
    private static IngestProfile Profile(bool numberedClauses) => new()
    {
        DocId = "lpa",
        Title = "LPA",
        Parts = [new PartRange("Document", "", 1, 10)],
        NumberedClauses = numberedClauses
    };

    private static LayoutBlock Block(int order, double left, double top, params string[] lines)
    {
        var layoutLines = lines.Select((text, i) =>
        {
            var y = top - 12 * (i + 1);
            var words = text.Split(' ').Select((w, j) => new LayoutWord(w, new Box(left + j * 30, y, left + j * 30 + 25, y + 8), "Times", 10)).ToArray();
            return new LayoutLine(new Box(left, y, left + text.Length * 5, y + 8), words);
        }).ToArray();

        return new LayoutBlock(order, Box.Union(layoutLines.Select(l => l.Box)), layoutLines);
    }

    // Clause numbers in their own column, as a contract exported from Word comes out.
    private static readonly PageLayout Page = new(18, 612, 792,
    [
        Block(0, 72, 600, "2.", "2.1", "2.1.1"),
        Block(1, 110, 600, "GENERAL PROVISIONS", "Name; Registered Office.", "The business of the Fund shall be conducted under the name ABC.")
    ]);

    [Fact]
    public void Turns_a_clause_number_column_back_into_numbered_paragraphs()
    {
        var profile = Profile(numberedClauses: true);
        var blocks = new PageClassifier(profile, new TextCleaner([])).Classify(Page, profile.Parts[0], 10).Blocks;

        Assert.Equal(
        [
            (BlockKind.Heading, "2. GENERAL PROVISIONS", 2),
            (BlockKind.Paragraph, "2.1 Name; Registered Office.", 0),
            (BlockKind.Paragraph, "2.1.1 The business of the Fund shall be conducted under the name ABC.", 0)
        ], blocks.Select(b => (b.Kind, b.Text, b.HeadingLevel)));
    }

    [Fact]
    public void Hand_tuned_profiles_keep_the_old_table_behaviour()
    {
        var profile = Profile(numberedClauses: false);
        var blocks = new PageClassifier(profile, new TextCleaner([])).Classify(Page, profile.Parts[0], 10).Blocks;

        Assert.Equal(BlockKind.Table, Assert.Single(blocks).Kind);
    }
}

public sealed class ChatModeTests
{
    [Fact]
    public void General_chat_has_no_documents_and_attaching_one_switches_back()
    {
        var session = ChatBot.Api.Domain.ChatSession.Create();
        Assert.True(session.UseDocuments);

        session.SetDocuments([], useDocuments: false);
        Assert.False(session.UseDocuments);
        Assert.Empty(session.DocumentIds);

        session.SetDocuments(["report"], useDocuments: false);
        Assert.True(session.UseDocuments);
        Assert.Equal(["report"], session.DocumentIds);
    }
}
