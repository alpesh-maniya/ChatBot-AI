namespace ChatBot.Api.Domain;

// Immutable: a turn is appended to and never edited, which keeps the history an accurate
// record of what the model was actually shown.
public sealed record ChatMessage
{
    public required Guid Id { get; init; }
    public required ChatRole Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Sources the answer was grounded on. Empty for user turns and ungrounded replies.</summary>
    public IReadOnlyList<SourceRef> Sources { get; init; } = [];

    /// <summary>Source numbers the reply actually cites with [n].</summary>
    public IReadOnlyList<int> CitedSources { get; init; } = [];

    public static ChatMessage Create(ChatRole role, string content) => new()
    {
        Id = Guid.NewGuid(),
        Role = role,
        Content = content,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
