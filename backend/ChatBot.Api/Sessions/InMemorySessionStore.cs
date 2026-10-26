using System.Collections.Concurrent;
using ChatBot.Api.Domain;

namespace ChatBot.Api.Sessions;

// History lives in process memory and dies with it. Fine for a single node, and
// ISessionStore is where a durable store goes instead.
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<Guid, ChatSession> _sessions = new();

    public ChatSession Create()
    {
        var session = ChatSession.Create();
        _sessions[session.Id] = session;
        return session;
    }

    public bool TryGet(Guid id, out ChatSession session)
        => _sessions.TryGetValue(id, out session!);

    public IReadOnlyList<ChatSession> List()
        => _sessions.Values.OrderByDescending(s => s.LastActivityAt).ToArray();

    public bool Delete(Guid id) => _sessions.TryRemove(id, out _);

    public int EvictIdle(TimeSpan idleFor)
    {
        var cutoff = DateTimeOffset.UtcNow - idleFor;
        var stale = _sessions.Where(kvp => kvp.Value.LastActivityAt < cutoff)
                             .Select(kvp => kvp.Key)
                             .ToArray();

        var removed = 0;
        foreach (var id in stale)
        {
            if (_sessions.TryRemove(id, out _)) removed++;
        }
        return removed;
    }
}
