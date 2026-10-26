using ChatBot.Api.Domain;
using ChatBot.Api.Llm;

namespace ChatBot.Api.Chat;

// A closed hierarchy so the transport (SSE here) stays a straight mapping with no logic
// in it.
public abstract record ChatStreamEvent
{
    /// <summary>Once, before generation: what went into the prompt and what got left out.</summary>
    public sealed record ContextPlanned(PromptPlan Plan) : ChatStreamEvent;

    /// <summary>Once, before the first chunk, when RAG is on. Grounded = false means the
    /// reply is the fixed "not found" message and Sources is empty.</summary>
    public sealed record SourcesRetrieved(IReadOnlyList<SourceRef> Sources, bool Grounded) : ChatStreamEvent;

    /// <summary>A fragment of the reply, not necessarily a whole word.</summary>
    public sealed record Chunk(string Text) : ChatStreamEvent;

    /// <summary>Once, after generation, with the stored message and timings.</summary>
    /// <param name="FirstTokenSeconds">Mostly prompt prefill on CPU; the number that
    /// grows with the size of the sources.</param>
    /// <param name="TokensPerSecond">Generation speed after the first token, so prefill
    /// doesn't drag it down.</param>
    public sealed record Completed(
        ChatMessage Message,
        int CompletionTokens,
        double ElapsedSeconds,
        double TokensPerSecond,
        int UncitedSentences = 0,
        double FirstTokenSeconds = 0) : ChatStreamEvent;
}
