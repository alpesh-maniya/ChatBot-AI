namespace ChatBot.Api.Llm;

// What ContextWindowPolicy needs to build a grounded prompt. Sources are in rank order;
// when the window is tight the policy drops them from the end.
/// <param name="AnswerInstruction">Repeated after the question. A 1.5B model follows what
/// is closest to where it starts writing far better than the system prompt alone.</param>
/// <param name="FollowUpOf">The earlier question when this one only makes sense with it
/// ("And in FY 2017?"). Shown next to the question; the history alone isn't enough for a
/// small model to carry "revenues" across.</param>
public sealed record GroundingContext(
    string SystemPrompt,
    IReadOnlyList<GroundingSource> Sources,
    int? MaxResponseTokens,
    string? AnswerInstruction = null,
    string? FollowUpOf = null);

/// <param name="Label">Where it came from, as the model and the reader see it:
/// "GPO's Finances > FY 2018 Financial Results, Annual Report p. 23".</param>
public sealed record GroundingSource(int Number, string Label, string Text);

/// <summary>Per-request overrides of the Llama sampling settings. Null = keep the configured value.</summary>
public sealed record SamplingOverrides(float? Temperature, float? TopP, int? TopK, float? RepeatPenalty);
