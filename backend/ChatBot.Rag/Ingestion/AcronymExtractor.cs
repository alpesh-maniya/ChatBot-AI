using System.Text.RegularExpressions;
using ChatBot.Rag.Documents;

namespace ChatBot.Rag.Ingestion;

// Builds the acronym -> expansion map used to widen keyword queries. Two sources:
// the abbreviations page at the back of the report, and definitions written inline
// as "Agency Distribution Services (ADS)". The glossary wins when both exist.
public static partial class AcronymExtractor
{
    private static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "and", "the", "for", "on", "in", "to", "&", "a"
    };

    public static IReadOnlyDictionary<string, string> Extract(IEnumerable<SourceBlock> blocks)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = blocks.ToList();

        foreach (var block in list.Where(b => b.Kind == BlockKind.Glossary))
        {
            foreach (var row in block.Rows)
            {
                var cells = row.Split(" | ", 2);
                if (cells.Length < 2) continue;

                var acronym = cells[0].Split(' ')[0].Trim();
                if (GlossaryKey().IsMatch(acronym) && !map.ContainsKey(acronym))
                {
                    map[acronym] = cells[1].Trim();
                }
            }
        }

        foreach (var block in list.Where(b => b.Kind == BlockKind.Paragraph))
        {
            foreach (Match m in Inline().Matches(block.Text))
            {
                var acronym = m.Groups[1].Value;
                if (map.ContainsKey(acronym)) continue;

                var expansion = Expand(block.Text[..m.Index], acronym);
                if (expansion is not null) map[acronym] = expansion;
            }
        }

        return map;
    }

    // Walk back from "(ADS)" collecting capitalised words until their initials line up
    // with the acronym. "Federal Digital System (FDsys)" needs three words for two
    // capitals, which is why the first initial has to match too.
    internal static string? Expand(string before, string acronym)
    {
        var upper = acronym.Count(char.IsUpper);
        var words = before.TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taken = new List<string>();
        var capitals = 0;

        for (var i = words.Length - 1; i >= 0 && taken.Count < 9; i--)
        {
            var word = words[i].Trim('"', '\'', ',', ';', ':');
            if (word.Length == 0) return null;

            if (char.IsUpper(word[0]))
            {
                taken.Insert(0, word);
                capitals++;
                if (capitals >= upper && char.ToUpperInvariant(word[0]) == char.ToUpperInvariant(acronym[0]))
                {
                    return string.Join(' ', taken);
                }
            }
            else if (Joiners.Contains(word) && taken.Count > 0)
            {
                taken.Insert(0, word);
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\(([A-Z][A-Za-z&]{1,7})\)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"^[A-Z][A-Za-z&.]{1,9}$")]
    private static partial Regex GlossaryKey();
}
