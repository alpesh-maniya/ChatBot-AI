using ChatBot.Api.Domain;
using ChatBot.Api.Llm;

namespace ChatBot.Api.Chat;

// A closed hierarchy so the transport (SSE here) stays a straight mapping with no logic
// in it.
public abstract record ChatStreamEvent
{
    /// <summary>Once, before generation: what went into the prompt and what got left out.</summary>
    public sealed record ContextPlanned(PromptPlan Plan) : ChatStreamEvent;

    /// <summary>A fragment of the reply, not necessarily a whole word.</summary>
    public sealed record Chunk(string Text) : ChatStreamEvent;

    /// <summary>Once, after generation, with the stored message and timings.</summary>
    public sealed record Completed(
        ChatMessage Message,
        int CompletionTokens,
        double ElapsedSeconds,
        double TokensPerSecond) : ChatStreamEvent;
}
