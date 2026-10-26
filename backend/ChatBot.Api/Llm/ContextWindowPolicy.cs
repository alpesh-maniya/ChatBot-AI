using System.Text;
using System.Text.RegularExpressions;
using ChatBot.Api.Domain;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Llm;

// Picks the past turns the model sees on this request.
//
//   [ system ][ ...as many recent turns as fit... ][ primer ][ response budget ]
//   |<-------------------------- ContextSize ------------------------------->|
//
// The window is fixed and the conversation isn't, so something has to choose. Doing it
// here means the choice is measurable instead of being whatever llama.cpp does when it
// overflows (which is produce garbage, not throw).
//
// With grounding, the retrieved sources go inside the current user turn and are paid
// for before any history:
//
//   [ system ][ history... ][ user: Sources [1]..[k] + Question ][ primer ][ response ]
public sealed partial class ContextWindowPolicy(
    IChatTemplate template,
    IOptions<LlamaOptions> llamaOptions,
    IOptions<ChatOptions> chatOptions)
{
    private readonly LlamaOptions _llama = llamaOptions.Value;
    private readonly ChatOptions _chat = chatOptions.Value;

    public PromptPlan Compose(IReadOnlyList<ChatMessage> history, ITokenCounter tokens) =>
        Compose(history, tokens, grounding: null);

    public PromptPlan Compose(IReadOnlyList<ChatMessage> history, ITokenCounter tokens, GroundingContext? grounding)
    {
        var responseBudget = grounding?.MaxResponseTokens ?? _llama.MaxResponseTokens;
        var available = (int)_llama.ContextSize - responseBudget - _llama.SafetyMarginTokens;

        var systemBlock = template.RenderMessage(ChatRole.System, grounding?.SystemPrompt ?? _chat.SystemPrompt);
        var primer = template.RenderGenerationPrimer();

        // System prompt and primer come out of the budget first and are never evicted.
        // Packed like ordinary messages they'd be the oldest thing in the prompt, so the
        // bot would drop its own instructions exactly when the conversation got long.
        var fixedCost = tokens.Count(systemBlock) + tokens.Count(primer);

        // Grounded: the current question plus as many sources as fit is also fixed cost.
        string? groundedTurn = null;
        var sourceCount = 0;
        var sourceTokens = 0;
        var truncated = false;

        if (grounding is not null && history.Count > 0 && history[^1].Role == ChatRole.User)
        {
            var question = history[^1].Content;
            var instruction = grounding.AnswerInstruction;
            var questionOnly = tokens.Count(template.RenderMessage(ChatRole.User, RenderGrounded([], question, instruction, grounding.FollowUpOf)));

            // Lowest-ranked sources go first. The question never does.
            for (sourceCount = grounding.Sources.Count; sourceCount > 0; sourceCount--)
            {
                var block = template.RenderMessage(ChatRole.User, RenderGrounded(grounding.Sources.Take(sourceCount), question, instruction, grounding.FollowUpOf));
                var cost = tokens.Count(block);
                if (fixedCost + cost <= available)
                {
                    groundedTurn = block;
                    sourceTokens = cost - questionOnly;
                    break;
                }
            }

            if (groundedTurn is null)
            {
                var fitted = FitToBudget(history[^1], available - fixedCost, tokens, template);
                groundedTurn = fitted ?? template.RenderMessage(ChatRole.User, question);
                truncated = fitted is not null;
            }

            fixedCost += tokens.Count(groundedTurn);
            history = history.Take(history.Count - 1).ToArray();
        }

        var historyBudget = available - fixedCost;

        if (historyBudget < 0 || (historyBudget == 0 && groundedTurn is null))
        {
            // Config problem, not a runtime one. Better to fail on the first request than
            // to quietly serve a bot with no memory at all.
            throw new InvalidOperationException(
                $"Context budget exhausted before any history could be added. " +
                $"ContextSize={_llama.ContextSize}, MaxResponseTokens={responseBudget}, " +
                $"SafetyMargin={_llama.SafetyMarginTokens}, system prompt + primer={fixedCost} tokens. " +
                $"Increase Llama:ContextSize or shorten the system prompt.");
        }

        var selected = new List<string>();
        var remaining = historyBudget;
        var included = 0;

        // A grounded question that stands on its own gets no history. The earlier answers
        // are about other passages, and a 1.5B model will happily repeat one of their
        // figures as the answer to this question.
        var keepHistory = grounding is null || grounding.FollowUpOf is not null;

        // Newest first - follow-up questions depend on recency.
        for (var i = history.Count - 1; keepHistory && i >= 0; i--)
        {
            var message = history[i];

            // Old answers cite sources that are no longer in the prompt; their [2] would
            // only confuse the numbering of this turn's sources.
            var content = grounding is null ? message.Content : StripCitations(message.Content);
            var block = template.RenderMessage(message.Role, content);

            // Rendered and tokenised, not estimated from length: the delimiters are real
            // tokens and on a short turn they're a decent slice of the cost.
            var cost = tokens.Count(block);

            if (cost <= remaining)
            {
                selected.Add(block);
                remaining -= cost;
                included++;
                continue;
            }

            // The newest message on its own blows the budget. Dropping it would mean
            // ignoring what the user just typed, so trim it and flag it instead.
            if (i == history.Count - 1 && groundedTurn is null)
            {
                var fitted = FitToBudget(message, remaining, tokens, template);
                if (fitted is not null)
                {
                    selected.Add(fitted);
                    remaining = 0;
                    included++;
                    truncated = true;
                }
            }

            // Stop here rather than skipping this message to look for smaller older ones.
            // Skipping leaves gaps, and a transcript with gaps reads as non-sequiturs.
            break;
        }

        selected.Reverse();

        var prompt = new StringBuilder(systemBlock);
        foreach (var block in selected) prompt.Append(block);
        if (groundedTurn is not null)
        {
            prompt.Append(groundedTurn);
            included++;
        }
        prompt.Append(primer);

        var text = prompt.ToString();
        var totalMessages = history.Count + (groundedTurn is null ? 0 : 1);

        return new PromptPlan
        {
            Prompt = text,
            IncludedMessages = included,
            DroppedMessages = totalMessages - included,
            PromptTokens = tokens.Count(text),
            ResponseBudget = responseBudget,
            ContentTruncated = truncated,
            SourceCount = sourceCount,
            SourceTokens = sourceTokens,
            DroppedSources = grounding is null ? 0 : grounding.Sources.Count - sourceCount
        };
    }

    // The sources sit directly above the question, in the user's own turn. A small model
    // attends to what is closest to where it starts writing; instructions to use them
    // live in the system prompt.
    public static string RenderGrounded(
        IEnumerable<GroundingSource> sources,
        string question,
        string? instruction = null,
        string? followUpOf = null)
    {
        var sb = new StringBuilder();
        var list = sources.ToList();

        if (list.Count > 0)
        {
            sb.Append("Sources:\n");
            foreach (var s in list)
            {
                sb.Append('[').Append(s.Number).Append("] (").Append(s.Label).Append(")\n");
                sb.Append(s.Text.Trim()).Append("\n\n");
            }
        }

        sb.Append("Question: ").Append(question.Trim());
        if (!string.IsNullOrWhiteSpace(followUpOf))
        {
            sb.Append("\n(This follows the earlier question: \"").Append(followUpOf.Trim()).Append("\")");
        }
        if (!string.IsNullOrWhiteSpace(instruction)) sb.Append("\n\n").Append(instruction.Trim());
        return sb.ToString();
    }

    public static string StripCitations(string text) => CitationMarker().Replace(text, "");

    // Binary search on character length rather than slicing tokens: getting a string back
    // from a truncated token sequence needs a detokenise round trip that isn't reliably
    // lossless on a partial sequence. This costs a few extra Count calls and is correct.
    private static string? FitToBudget(ChatMessage message, int budget, ITokenCounter tokens, IChatTemplate template)
    {
        const string Marker = "\n\n[… earlier part of this message omitted to fit the context window …]";

        if (budget <= 0) return null;

        var content = message.Content;
        int low = 0, high = content.Length, best = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            // Keep the tail - in a long paste the question is usually at the end.
            var candidate = Marker + content[^mid..];
            var block = template.RenderMessage(message.Role, candidate);

            if (tokens.Count(block) <= budget)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (best <= 0) return null;
        return template.RenderMessage(message.Role, Marker + content[^best..]);
    }

    [GeneratedRegex(@"\s?\[\d+(?:\s*,\s*\d+)*\]")]
    private static partial Regex CitationMarker();
}
