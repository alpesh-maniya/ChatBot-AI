using ChatBot.Rag.Chunking;
using ChatBot.Rag.Ingestion;

namespace ChatBot.Rag.Documents;

// manifest.json. The API checks it against the loaded embedder before serving anything,
// so "upgraded the model, forgot to re-ingest" is a startup error rather than quietly
// meaningless similarity scores.
public sealed record DocumentManifest
{
    // Bump whenever chunk boundaries or chunk text can change for the same PDF.
    public const int CurrentChunkerVersion = 2;

    public required string DocId { get; init; }
    public required string Title { get; init; }
    public required string SourceFile { get; init; }
    public required string SourceSha256 { get; init; }
    public required int PdfPageCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public required string EmbeddingModel { get; init; }
    public required int EmbeddingDimensions { get; init; }

    public required int ChunkerVersion { get; init; }
    public required ChunkingOptions Chunking { get; init; }
    public required int ChunkCount { get; init; }

    public IReadOnlyList<PartInfo> Parts { get; init; } = [];

    // Display only; a profile beside the PDF overrides these at load time, so changing
    // them doesn't need a re-ingest.
    public string? Subject { get; init; }
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    // Read at question time, not ingest time, so it is overridden the same way. Stored
    // here so a tool that loads the index without the profile (Eval) routes the same.
    public IReadOnlyList<SectionRoute> Routes { get; init; } = [];
    public bool SectionLead { get; init; }
}

public sealed record PartInfo(string Name, string Label, int FromPage, int ToPage);
