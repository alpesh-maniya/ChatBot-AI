using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ChatBot.Api.Domain;
using ChatBot.Api.Llm;
using ChatBot.Api.Sessions;

namespace ChatBot.Api.Chat;

// Order of operations for answering a message:
//
//   1. append the user turn to the session
//   2. ask the policy what fits in the window
//   3. stream the completion
//   4. append the assistant turn
//
// Step 4 is what makes follow-ups work. "What are its main components?" only resolves
// because steps 1 and 4 of the previous turn are still in the session when the policy
// runs for this one.
public sealed class ChatService(
    ISessionStore sessions,
    ContextWindowPolicy policy,
    ILanguageModel model,
    ILogger<ChatService> logger)
{
    public async IAsyncEnumerable<ChatStreamEvent> SendAsync(
        Guid sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!sessions.TryGet(sessionId, out var session))
        {
            throw new SessionNotFoundException(sessionId);
        }

        session.Append(ChatMessage.Create(ChatRole.User, userMessage));

        var plan = policy.Compose(session.Snapshot(), model);

        logger.LogDebug(
            "Session {SessionId}: {Included} of {Total} turns in context, {Tokens} prompt tokens.",
            sessionId, plan.IncludedMessages, session.MessageCount, plan.PromptTokens);

        // Trace gives you the literal prompt, which is the first thing to look at when
        // the bot answers as though it forgot something.
        logger.LogTrace("Session {SessionId} prompt:\n{Prompt}", sessionId, plan.Prompt);

        yield return new ChatStreamEvent.ContextPlanned(plan);

        var reply = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        ChatMessage assistantMessage;

        try
        {
            await foreach (var chunk in model.StreamAsync(plan.Prompt, plan.ResponseBudget, cancellationToken))
            {
                reply.Append(chunk);
                yield return new ChatStreamEvent.Chunk(chunk);
            }
        }
        finally
        {
            stopwatch.Stop();

            // Close the turn no matter how generation ended. A partial answer is still
            // worth keeping, and the transcript has to stay user/assistant alternating -
            // two user turns in a row is a prompt shape the model never saw in training.
            var text = reply.ToString().Trim();
            if (text.Length == 0)
            {
                text = "(no response generated)";
                logger.LogWarning("Session {SessionId}: model produced no output.", sessionId);
            }

            assistantMessage = ChatMessage.Create(ChatRole.Assistant, text);
            session.Append(assistantMessage);
        }

        var completionTokens = model.Count(assistantMessage.Content);
        var seconds = stopwatch.Elapsed.TotalSeconds;

        yield return new ChatStreamEvent.Completed(
            Message: assistantMessage,
            CompletionTokens: completionTokens,
            ElapsedSeconds: seconds,
            TokensPerSecond: seconds > 0 ? completionTokens / seconds : 0);
    }
}

public sealed class SessionNotFoundException(Guid sessionId)
    : Exception($"Session '{sessionId}' does not exist or has expired.")
{
    public Guid SessionId { get; } = sessionId;
}
