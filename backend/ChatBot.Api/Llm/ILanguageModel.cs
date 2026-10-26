namespace ChatBot.Api.Llm;

// Everything LLamaSharp-specific stays behind this, so moving to a hosted endpoint
// later wouldn't reach the controllers.
public interface ILanguageModel : ITokenCounter
{
    /// <summary>False while the weights load. Chat requests get a 503 until it flips.</summary>
    bool IsReady { get; }

    ModelInfo Info { get; }

    /// <summary>Streams a completion for an already-templated prompt. Yields decoded
    /// fragments, not whole words.</summary>
    IAsyncEnumerable<string> StreamAsync(string prompt, int maxTokens, CancellationToken cancellationToken);
}

public sealed record ModelInfo(string Name, uint ContextSize, ulong ParameterCount, bool Ready, string? LoadError);
