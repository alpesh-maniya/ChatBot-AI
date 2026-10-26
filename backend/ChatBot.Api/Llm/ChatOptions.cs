namespace ChatBot.Api.Llm;

/// <summary>Bound from the "Chat" section of appsettings.json.</summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    // In config rather than a const so it can be tuned without a rebuild - prompt
    // wording needs a lot of iteration.
    public string SystemPrompt { get; set; } = "";

    public int SessionIdleTimeoutMinutes { get; set; } = 120;

    /// <summary>Rejects oversized input before it reaches the tokenizer.</summary>
    public int MaxUserMessageCharacters { get; set; } = 8000;
}
