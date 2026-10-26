using ChatBot.Rag.Chunking;

namespace ChatBot.Rag.Embeddings;

// Turns text into a point in "meaning space". Vectors come back L2-normalised, so the
// dot product of two of them is their cosine similarity.
public interface IEmbeddingModel : ITextTokenizer
{
    string Name { get; }
    int Dimensions { get; }
    bool IsReady { get; }

    /// <summary>Longest input in tokens; ingestion refuses passages over it.</summary>
    int ContextLimit { get; }

    Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken);

    /// <summary>Adds the model's query instruction first. bge is trained with
    /// asymmetric prompts: questions get a prefix, passages don't.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken);
}

public sealed record EmbeddingOptions
{
    public string ModelPath { get; set; } = "";
    public string QueryPrefix { get; set; } = "Represent this sentence for searching relevant passages: ";
    public string DocumentPrefix { get; set; } = "";

    /// <summary>bge-small's position limit. Chunks are sized to fit under it.</summary>
    public uint ContextSize { get; set; } = 512;

    public int? Threads { get; set; }
}
