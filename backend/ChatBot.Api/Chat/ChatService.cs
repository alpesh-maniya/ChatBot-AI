using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ChatBot.Api.Domain;
using ChatBot.Api.Llm;
using ChatBot.Api.Rag;
using ChatBot.Api.Sessions;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Chat;

// Order of operations for answering a message:
//
//   1. append the user turn to the session
//   2. (RAG) work out which documents this conversation is about - the attached ones, or
//      all of them - and retrieve passages from those; if none are relevant, reply with
//      the fixed "not found" message and stop - the LLM is never asked
//   3. ask the policy what fits in the window
//   4. stream the completion
//   5. append the assistant turn (with its sources)
//
// Step 5 is what makes follow-ups work. "What are its main components?" only resolves
// because steps 1 and 5 of the previous turn are still in the session when the policy
// runs for this one.
public sealed partial class ChatService(
    ISessionStore sessions,
    ContextWindowPolicy policy,
    ILanguageModel model,
    RagRuntime rag,
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

        // General chat, and greetings or thanks in a document chat: no retrieval. Answering
        // "hello" from a document makes the bot say "I couldn't find 'hello' in the report".
        if (!rag.AnswersFromDocuments(session.UseDocuments, session.DocumentIds) || QueryBuilder.IsSmallTalk(userMessage))
        {
            // No "sources" event: nothing was searched, and grounded=false is what the UI
            // reads as "not found in your documents".
            session.Append(ChatMessage.Create(ChatRole.User, userMessage));

            await foreach (var e in GenerateAsync(session, grounding: null, [], null, cancellationToken))
            {
                yield return e;
            }
            yield break;
        }

        // Before the user turn is stored, so a refusal here leaves the transcript as it was.
        var (scope, problem) = rag.ResolveScope(session.DocumentIds);
        if (scope is null) throw new RagNotReadyException(problem);

        var previousQuestion = session.Snapshot().LastOrDefault(m => m.Role == ChatRole.User)?.Content;
        session.Append(ChatMessage.Create(ChatRole.User, userMessage));

        var retrieval = await scope.Retriever.RetrieveAsync(userMessage, previousQuestion, cancellationToken);

        logger.LogDebug(
            "Session {SessionId}: retrieval over {Docs} doc(s) {Ms:N0}ms, top cosine {Top:F3}, grounded {Grounded}, query \"{Query}\"{Previous}, sources {Sources}.",
            sessionId, scope.Documents.Count, retrieval.ElapsedMilliseconds, retrieval.TopVectorScore, retrieval.Grounded,
            retrieval.Query.Vector, retrieval.Query.UsedPreviousTurn ? " (with previous turn)" : "",
            string.Join(", ", retrieval.Sources.Select(s => s.Chunk.Id)));

        if (!retrieval.Grounded || retrieval.Sources.Count == 0)
        {
            foreach (var e in Fallback(session, scope.FallbackMessage)) yield return e;
            yield break;
        }

        var titles = scope.Documents.ToDictionary(d => d.DocId, d => d.Title, StringComparer.OrdinalIgnoreCase);
        var refs = retrieval.Sources.Select(s => ToSourceRef(s, titles.GetValueOrDefault(s.Chunk.DocId, s.Chunk.DocId))).ToList();

        // With several documents in scope the model has to be told which one a passage is
        // from, or "revenue was $186.4M" gets attributed to the wrong company.
        var multiDocument = scope.Documents.Count > 1;
        var maxChars = rag.Options.Retrieval.PassageMaxChars;
        var grounding = new GroundingContext(
            scope.SystemPrompt,
            retrieval.Sources.Zip(refs, (s, r) => new GroundingSource(
                r.Number,
                multiDocument ? $"{r.DocTitle}: {r.Label}" : r.Label,
                PassageTrimmer.Trim(
                    s.Chunk.Unit == "in thousands" ? ExpressThousands(s.Chunk.Text) : s.Chunk.Text,
                    retrieval.Query.Keyword,
                    s.Number <= rag.Options.Retrieval.WholePassages ? 0 : maxChars))).ToList(),
            rag.Options.Sampling.MaxResponseTokens,
            scope.AnswerInstruction,
            retrieval.Query.UsedPreviousTurn ? previousQuestion : null);

        await foreach (var e in GenerateAsync(session, grounding, refs, scope.FallbackMessage, cancellationToken))
        {
            yield return e;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> GenerateAsync(
        ChatSession session,
        GroundingContext? grounding,
        IReadOnlyList<SourceRef> sources,
        string? fallbackMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plan = policy.Compose(session.Snapshot(), model, grounding);

        if (grounding is not null && plan.SourceCount == 0)
        {
            // Not even one source fits next to the question. Answering ungrounded would
            // be worse than admitting it.
            logger.LogWarning("Session {SessionId}: no source fits in the context window.", session.Id);
            foreach (var e in Fallback(session, fallbackMessage ?? "")) yield return e;
            yield break;
        }

        var sent = sources.Take(plan.SourceCount).ToList();

        logger.LogDebug(
            "Session {SessionId}: {Included} of {Total} turns in context, {Tokens} prompt tokens, {Sources} sources ({SourceTokens} tokens, {Dropped} dropped).",
            session.Id, plan.IncludedMessages, session.MessageCount, plan.PromptTokens, plan.SourceCount, plan.SourceTokens, plan.DroppedSources);

        // Trace gives you the literal prompt, which is the first thing to look at when
        // the bot answers as though it forgot something.
        logger.LogTrace("Session {SessionId} prompt:\n{Prompt}", session.Id, plan.Prompt);

        yield return new ChatStreamEvent.ContextPlanned(plan);
        if (grounding is not null) yield return new ChatStreamEvent.SourcesRetrieved(sent, Grounded: true);

        var sampling = grounding is null
            ? null
            : new SamplingOverrides(rag.Options.Sampling.Temperature, rag.Options.Sampling.TopP,
                                    rag.Options.Sampling.TopK, rag.Options.Sampling.RepeatPenalty);

        var reply = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? firstToken = null;
        ChatMessage assistantMessage;
        var uncited = 0;

        try
        {
            await foreach (var chunk in model.StreamAsync(plan.Prompt, plan.ResponseBudget, cancellationToken, sampling))
            {
                firstToken ??= stopwatch.Elapsed;
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
                logger.LogWarning("Session {SessionId}: model produced no output.", session.Id);
            }

            IReadOnlyList<int> cited = [];
            if (grounding is not null)
            {
                var check = CitationValidator.Check(text, grounding.Sources.Take(sent.Count).Select(s => s.Text).ToList());
                if (check.RemovedMarkers > 0 || check.AutoCited > 0)
                {
                    logger.LogInformation(
                        "Session {SessionId}: removed {Removed} citation(s) to sources that were not sent, added {Added} for uncited figures.",
                        session.Id, check.RemovedMarkers, check.AutoCited);
                }
                text = check.Text;
                cited = check.Cited;
                uncited = check.UncitedSentences;

                // Nothing but markers ("[2]"). Seen on contracts, whose numbered clauses
                // and "[Fund Manager]" placeholders look like citations to a small model.
                if (CitationValidator.IsOnlyMarkers(text) && !cancellationToken.IsCancellationRequested)
                {
                    var question = session.Snapshot().LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? "";
                    var quote = CitationValidator.QuoteBest(question, grounding.Sources.Take(sent.Count).Select(s => s.Text).ToList());
                    logger.LogWarning("Session {SessionId}: reply had no words (\"{Reply}\"); {Action}.",
                        session.Id, text, quote is null ? "using the fallback message" : $"quoting source {quote.Value.Source}");

                    if (quote is { } q)
                    {
                        text = $"From the source: \"{q.Sentence}\" [{q.Source}]";
                        cited = [q.Source];
                    }
                    else
                    {
                        text = fallbackMessage ?? text;
                        cited = [];
                    }
                }
            }

            assistantMessage = ChatMessage.Create(ChatRole.Assistant, text) with { Sources = sent, CitedSources = cited };
            session.Append(assistantMessage);
        }

        var completionTokens = model.Count(assistantMessage.Content);
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var ttft = firstToken?.TotalSeconds ?? seconds;
        var generating = seconds - ttft;

        logger.LogDebug("Session {SessionId}: first token after {Ttft:N1}s, {Tokens} tokens in {Seconds:N1}s.",
            session.Id, ttft, completionTokens, seconds);

        yield return new ChatStreamEvent.Completed(
            Message: assistantMessage,
            CompletionTokens: completionTokens,
            ElapsedSeconds: seconds,
            TokensPerSecond: generating > 0.05 ? Math.Max(0, completionTokens - 1) / generating : 0,
            UncitedSentences: uncited,
            FirstTokenSeconds: ttft);
    }

    // The documents don't cover it. Saying so in fixed words is more reliable than hoping
    // a 1.5B model declines on its own.
    private IEnumerable<ChatStreamEvent> Fallback(ChatSession session, string text)
    {
        var message = ChatMessage.Create(ChatRole.Assistant, text);
        session.Append(message);

        yield return new ChatStreamEvent.SourcesRetrieved([], Grounded: false);
        yield return new ChatStreamEvent.Chunk(text);
        yield return new ChatStreamEvent.Completed(message, model.Count(text), 0, 0);
    }

    private static SourceRef ToSourceRef(RetrievedSource s, string docTitle)
    {
        var c = s.Chunk;
        return new SourceRef(
            Number: s.Number,
            ChunkId: c.Id,
            DocId: c.DocId,
            Part: c.Part,
            PartLabel: c.PartLabel,
            Section: c.Section,
            PrintedPages: c.PrintedPages,
            PdfPageStart: c.PdfPageStart,
            PdfPageEnd: c.PdfPageEnd,
            Excerpt: Excerpt(c.Text, 240),
            VectorScore: s.VectorScore,
            Bm25Rank: s.Bm25Rank,
            FusedScore: s.FusedScore,
            DocTitle: docTitle);
    }

    // "(Dollars in Thousands)" in a table title is not enough: the model copies "874,466"
    // and calls it dollars, and a note explaining the unit didn't change that. So the
    // model is shown the figures already converted: "$ 874,466" -> "$874.5 million",
    // "(28,745)" -> "-$28.7 million". Only the prompt changes; the stored chunk and the
    // excerpt the user sees keep the report's own numbers.
    internal static string ExpressThousands(string text)
    {
        var converted = ThousandsFigure().Replace(text, m =>
        {
            var thousands = decimal.Parse(m.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture);
            var millions = thousands / 1000m;
            var negative = m.Groups[1].Success && m.Groups[3].Success;
            var amount = millions >= 1000
                ? $"${(millions / 1000m).ToString("#,0.00", CultureInfo.InvariantCulture)} billion"
                : $"${millions.ToString("#,0.0", CultureInfo.InvariantCulture)} million";
            return (negative ? "-" : "") + amount;
        });

        return "(Figures converted from the report's thousands of dollars.)\n" + converted;
    }

    [GeneratedRegex(@"(\()?(?:\$\s?)?(\d{1,3}(?:,\d{3})+)(\))?")]
    private static partial Regex ThousandsFigure();

    private static string Excerpt(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ");
        if (flat.Length <= max) return flat;
        var cut = flat.LastIndexOf(' ', max);
        return flat[..(cut > max / 2 ? cut : max)] + "…";
    }
}

public sealed class SessionNotFoundException(Guid sessionId)
    : Exception($"Session '{sessionId}' does not exist or has expired.")
{
    public Guid SessionId { get; } = sessionId;
}

public sealed class RagNotReadyException(string? reason)
    : Exception(reason ?? "The document index is still loading. Try again in a moment.");
