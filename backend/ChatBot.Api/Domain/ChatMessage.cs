namespace ChatBot.Api.Domain;

// Immutable: a turn is appended to and never edited, which keeps the history an accurate
// record of what the model was actually shown.
public sealed record ChatMessage
{
    public required Guid Id { get; init; }
    public required ChatRole Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static ChatMessage Create(ChatRole role, string content) => new()
    {
        Id = Guid.NewGuid(),
        Role = role,
        Content = content,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
