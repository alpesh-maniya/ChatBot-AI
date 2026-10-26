using System.Text.RegularExpressions;
using ChatBot.Rag.Index;

namespace ChatBot.Rag.Retrieval;

public sealed record SearchQuery(string Vector, string Keyword, bool UsedPreviousTurn);

// What to search for. The stored user message is not always a good query: "And in FY
// 2017?" or "Why did it go up?" mean nothing without the turn before. The cheap rule
// here (short or referring message -> prepend the previous question) avoids a second
// LLM pass on CPU just to rewrite the question.
public sealed partial class QueryBuilder(IReadOnlyDictionary<string, string> acronyms)
{
    private static readonly HashSet<string> ReferenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "its", "that", "this", "they", "them", "those", "these", "same", "previous", "compare", "compared",
        "why", "more", "also", "then", "there", "he", "she", "his", "her"
    };

    private static readonly string[] SmallTalk =
    [
        "hi", "hello", "hey", "thanks", "thank you", "thx", "ok", "okay", "great", "good morning", "good afternoon",
        "good evening", "bye", "goodbye", "cool", "nice", "who are you", "what can you do", "help"
    ];

    public static bool IsSmallTalk(string message)
    {
        var text = Punctuation().Replace(message.Trim().ToLowerInvariant(), "").Trim();
        return text.Length == 0 || SmallTalk.Contains(text);
    }

    public SearchQuery Build(string message, string? previousUserMessage)
    {
        var usePrevious = Continues(message) && !string.IsNullOrWhiteSpace(previousUserMessage);
        var vector = usePrevious ? $"{previousUserMessage!.Trim()} {message.Trim()}" : message.Trim();

        return new SearchQuery(vector, Expand(vector), usePrevious);
    }

    // Whether the message only makes sense with the question before it. A pronoun alone
    // isn't enough: in "What material is the iMac stand made from, and what percentage of
    // it is recycled?" the "it" points back into its own sentence, and joining that to an
    // unrelated earlier question searches for both. What counts is how much the message
    // says before its first reference word - "Why did inventory go up that year?" has
    // three content words there, the iMac question has five.
    public static bool Continues(string message)
    {
        if (message.TrimStart().StartsWith("and ", StringComparison.OrdinalIgnoreCase)) return true;

        var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Punctuation().Replace(w, "").ToLowerInvariant())
            .ToArray();
        var content = 0;
        foreach (var word in words)
        {
            if (ReferenceWords.Contains(word)) return content < OwnSubjectWords;
            if (IsContent(word)) content++;
        }

        // "In FY 2017?", "What about inventory?"
        return words.Length < 5 && content < 3;
    }

    private const int OwnSubjectWords = 4;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "about", "tell", "me", "please", "explain", "describe", "say", "says", "much", "many", "report", "document"
    };

    private static bool IsContent(string word) =>
        word.Length > 0 && !Filler.Contains(word) && Bm25Index.Tokenize(word).Count > 0;

    // BM25 only. "How did ADS do?" also searches "Agency Distribution Services", and a
    // question that spells the name out also searches the acronym.
    public string Expand(string query)
    {
        var extra = new List<string>();

        foreach (Match m in AcronymToken().Matches(query))
        {
            if (acronyms.TryGetValue(m.Value, out var expansion)) extra.Add(expansion);
        }

        foreach (var (acronym, expansion) in acronyms)
        {
            if (query.Contains(expansion, StringComparison.OrdinalIgnoreCase)) extra.Add(acronym);
        }

        return extra.Count == 0 ? query : query + " " + string.Join(' ', extra.Distinct());
    }

    [GeneratedRegex(@"[^\w\s']")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\b[A-Z][A-Za-z&]{1,7}\b")]
    private static partial Regex AcronymToken();
}
