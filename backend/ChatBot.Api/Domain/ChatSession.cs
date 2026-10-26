namespace ChatBot.Api.Domain;

// One conversation. This object is the conversation memory - there is no model-side
// state anywhere.
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages = [];
    private readonly Lock _gate = new();

    public required Guid Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>First user message, trimmed. Used as the sidebar label.</summary>
    public string? Title { get; private set; }

    public static ChatSession Create() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow
    };

    // A copy, because callers iterate this while inference runs for many seconds and a
    // concurrent Append would invalidate the enumerator.
    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_gate) return _messages.ToArray();
    }

    public void Append(ChatMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            LastActivityAt = DateTimeOffset.UtcNow;

            if (Title is null && message.Role == ChatRole.User)
            {
                Title = Summarise(message.Content);
            }
        }
    }

    public int MessageCount
    {
        get { lock (_gate) return _messages.Count; }
    }

    private static string Summarise(string content)
    {
        var text = content.Trim().ReplaceLineEndings(" ");
        return text.Length <= 48 ? text : string.Concat(text.AsSpan(0, 47), "…");
    }
}
