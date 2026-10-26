namespace ChatBot.Api.Llm;

// What the model was given this turn. Goes back to the client as well as the log:
// "why did it forget that?" is the usual complaint about a chatbot, and the answer is
// normally sitting in DroppedMessages.
public sealed record PromptPlan
{
    public required string Prompt { get; init; }

    /// <summary>History turns that made it in, excluding the system prompt.</summary>
    public required int IncludedMessages { get; init; }

    /// <summary>Turns evicted to fit. Non-zero means the model has forgotten something.</summary>
    public required int DroppedMessages { get; init; }

    public required int PromptTokens { get; init; }

    public required int ResponseBudget { get; init; }

    /// <summary>A single message was too big and had its content cut.</summary>
    public required bool ContentTruncated { get; init; }

    /// <summary>Retrieved passages that made it into the prompt. Zero when not grounded.</summary>
    public int SourceCount { get; init; }

    /// <summary>Prompt tokens spent on the Sources block.</summary>
    public int SourceTokens { get; init; }

    /// <summary>Retrieved passages left out because the window was full.</summary>
    public int DroppedSources { get; init; }
}
