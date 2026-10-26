using ChatBot.Api.Domain;

namespace ChatBot.Api.Sessions;

// Storage seam. Swapping the in-memory store for Redis or SQL is a registration change
// in Program.cs; nothing above this interface knows the difference.
public interface ISessionStore
{
    ChatSession Create();
    bool TryGet(Guid id, out ChatSession session);
    IReadOnlyList<ChatSession> List();
    bool Delete(Guid id);

    /// <summary>Drops sessions idle longer than <paramref name="idleFor"/>, returns how many.</summary>
    int EvictIdle(TimeSpan idleFor);
}
