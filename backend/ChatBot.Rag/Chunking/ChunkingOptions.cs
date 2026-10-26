namespace ChatBot.Rag.Chunking;

// Token counts are the embedding model's tokens, not Qwen's. bge-small reads at most 512,
// so MaxTokens plus the contextual header has to stay under that.
public sealed record ChunkingOptions
{
    public int TargetTokens { get; init; } = 350;
    public int MaxTokens { get; init; } = 450;
    public int OverlapTokens { get; init; } = 50;
    public int MinTokens { get; init; } = 60;
    public int TableMaxTokens { get; init; } = 450;
}

public interface ITextTokenizer
{
    int CountTokens(string text);
}
