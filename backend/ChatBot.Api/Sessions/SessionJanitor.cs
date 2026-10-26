using ChatBot.Api.Llm;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Sessions;

// Without this an in-memory store is an unbounded leak: every browser that ever opened
// the app keeps a conversation alive forever. One sweep beats a timer per session.
public sealed class SessionJanitor(
    ISessionStore store,
    IOptions<ChatOptions> options,
    ILogger<SessionJanitor> logger) : BackgroundService
{
    private readonly ChatOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(5);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var removed = store.EvictIdle(TimeSpan.FromMinutes(_options.SessionIdleTimeoutMinutes));
            if (removed > 0)
            {
                logger.LogInformation("Evicted {Count} idle session(s).", removed);
            }
        }
    }
}
