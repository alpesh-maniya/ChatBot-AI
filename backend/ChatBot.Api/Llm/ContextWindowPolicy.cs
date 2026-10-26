using System.Text;
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
public sealed class ContextWindowPolicy(
    IChatTemplate template,
    IOptions<LlamaOptions> llamaOptions,
    IOptions<ChatOptions> chatOptions)
{
    private readonly LlamaOptions _llama = llamaOptions.Value;
    private readonly ChatOptions _chat = chatOptions.Value;

    public PromptPlan Compose(IReadOnlyList<ChatMessage> history, ITokenCounter tokens)
    {
        var available = (int)_llama.ContextSize - _llama.MaxResponseTokens - _llama.SafetyMarginTokens;

        var systemBlock = template.RenderMessage(ChatRole.System, _chat.SystemPrompt);
        var primer = template.RenderGenerationPrimer();

        // System prompt and primer come out of the budget first and are never evicted.
        // Packed like ordinary messages they'd be the oldest thing in the prompt, so the
        // bot would drop its own instructions exactly when the conversation got long.
        var fixedCost = tokens.Count(systemBlock) + tokens.Count(primer);
        var historyBudget = available - fixedCost;

        if (historyBudget <= 0)
        {
            // Config problem, not a runtime one. Better to fail on the first request than
            // to quietly serve a bot with no memory at all.
            throw new InvalidOperationException(
                $"Context budget exhausted before any history could be added. " +
                $"ContextSize={_llama.ContextSize}, MaxResponseTokens={_llama.MaxResponseTokens}, " +
                $"SafetyMargin={_llama.SafetyMarginTokens}, system prompt + primer={fixedCost} tokens. " +
                $"Increase Llama:ContextSize or shorten Chat:SystemPrompt.");
        }

        var selected = new List<string>();
        var remaining = historyBudget;
        var truncated = false;
        var included = 0;

        // Newest first - follow-up questions depend on recency.
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            var block = template.RenderMessage(message.Role, message.Content);

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
            if (i == history.Count - 1)
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
        prompt.Append(primer);

        var text = prompt.ToString();

        return new PromptPlan
        {
            Prompt = text,
            IncludedMessages = included,
            DroppedMessages = history.Count - included,
            PromptTokens = tokens.Count(text),
            ResponseBudget = _llama.MaxResponseTokens,
            ContentTruncated = truncated
        };
    }

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
}
