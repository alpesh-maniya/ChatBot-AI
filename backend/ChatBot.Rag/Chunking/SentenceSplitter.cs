using System.Text.RegularExpressions;

namespace ChatBot.Rag.Chunking;

// Sentence boundaries: ". ? !" followed by a space and a capital, digit or quote.
// The annual report is full of things that look like boundaries and aren't:
// "U.S. Government", "No. 19-01", "$874.5 million", "Mr. Freddie W. Hall".
public static partial class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "U.S", "U. S", "Mr", "Mrs", "Ms", "Dr", "No", "Nos", "Fig", "Vol", "St", "Jr", "Sr", "Inc", "Co",
        "Corp", "Ltd", "Dept", "Gov", "vs", "etc", "e.g", "i.e", "Stat", "Sec", "U.S.C", "D.C", "a.m", "p.m", "approx"
    };

    public static IReadOnlyList<string> Split(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        foreach (Match m in Boundary().Matches(text))
        {
            // Keep a closing quote or bracket with the sentence it closes.
            var end = m.Index + m.Value.TrimEnd().Length;
            var candidate = text[start..end];

            if (EndsWithAbbreviation(candidate)) continue;

            var s = candidate.Trim();
            if (s.Length > 0) sentences.Add(s);
            start = m.Index + m.Length;
        }

        var rest = text[start..].Trim();
        if (rest.Length > 0) sentences.Add(rest);
        return sentences;
    }

    private static bool EndsWithAbbreviation(string candidate)
    {
        var trimmed = candidate.TrimEnd('.', '?', '!');
        var lastSpace = trimmed.LastIndexOf(' ');
        var word = lastSpace < 0 ? trimmed : trimmed[(lastSpace + 1)..];
        word = word.TrimStart('(', '"', '\'');

        if (Abbreviations.Contains(word)) return true;

        // Single initials: "John W. Crawford", "Brett M. Kavanaugh".
        return word.Length == 1 && char.IsUpper(word[0]);
    }

    [GeneratedRegex(@"[.?!][""')\]]?\s+(?=[A-Z0-9""'(\[$])")]
    private static partial Regex Boundary();
}
