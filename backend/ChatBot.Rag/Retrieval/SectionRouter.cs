using System.Text.RegularExpressions;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Ingestion;

namespace ChatBot.Rag.Retrieval;

/// <param name="Sections">The section names the question matched, for logs and the search API.</param>
/// <param name="Rows">Chunk rows under those sections; search is limited to them.</param>
public sealed record SectionFilter(IReadOnlyList<string> Sections, IReadOnlySet<int> Rows);

// "What is the growth outlook for South Asia?" - six regions in the World Bank report all
// talk about growth outlooks, and their wording is close enough that the wrong region can
// win on both cosine and BM25. When the question names a section, search only that
// section.
//
// Rules, not a model: the names come from the profile's routes, plus "Chapter N" against
// part titles. Only names the reader typed are matched, never topics, because a filter
// that guesses wrong removes the answer from the candidates entirely.
public sealed partial class SectionRouter
{
    private readonly IReadOnlyList<DocumentChunk> _chunks;
    private readonly List<(Regex Term, string Section)> _routes = [];

    public SectionRouter(IReadOnlyList<DocumentChunk> chunks, IEnumerable<SectionRoute> routes)
    {
        _chunks = chunks;
        foreach (var route in routes)
        foreach (var term in route.Terms.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            // "SAR" and "ECA" only as written; "south asia" in any case.
            var options = term.Any(char.IsLower) ? RegexOptions.IgnoreCase : RegexOptions.None;
            _routes.Add((new Regex($@"(?<![\w-]){Regex.Escape(term)}(?![\w-])", options | RegexOptions.CultureInvariant), route.Section));
        }
    }

    /// <summary>Null when the question names no section, or none that has chunks.</summary>
    public SectionFilter? Route(string question)
    {
        // "Chapter 4" and "Box 1.1" are matched only for documents whose profile set up
        // routes, i.e. whose headings were checked. On default rules a stray "CHAPTER 4"
        // divider heading made a filter of two chunks and hid the answer.
        if (_routes.Count == 0) return null;

        var sections = _routes.Where(r => r.Term.IsMatch(question)).Select(r => r.Section).Distinct().ToList();
        var chapters = ChapterNumber().Matches(question).Select(m => m.Groups[1].Value).Distinct().ToList();
        var boxes = BoxNumber().Matches(question).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value} ").Distinct().ToList();
        if (sections.Count == 0 && chapters.Count == 0 && boxes.Count == 0) return null;

        var rows = new HashSet<int>();
        var matched = new List<string>();
        for (var row = 0; row < _chunks.Count; row++)
        {
            var path = _chunks[row].SectionPath;
            var hit = sections.FirstOrDefault(s => path.Any(p => string.Equals(p, s, StringComparison.OrdinalIgnoreCase)))
                      ?? chapters.Select(n => path.Count > 0 && IsChapter(path[0], n) ? path[0] : null).FirstOrDefault(p => p is not null)
                      // "BOX 1.1 How much will AI affect global growth?", "ANNEX 3.1 Methodology and data"
                      ?? path.FirstOrDefault(p => boxes.Any(b => (p + " ").StartsWith(b, StringComparison.OrdinalIgnoreCase)));
            if (hit is null) continue;

            rows.Add(row);
            if (!matched.Contains(hit)) matched.Add(hit);
        }

        return rows.Count == 0 ? null : new SectionFilter(matched, rows);
    }

    // "Chapter 3: A Rising Challenge..." or plain "Chapter 3".
    private static bool IsChapter(string heading, string number) =>
        heading.StartsWith($"Chapter {number}:", StringComparison.OrdinalIgnoreCase)
        || string.Equals(heading, $"Chapter {number}", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bchapter\s+(\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterNumber();

    [GeneratedRegex(@"\b(box|annex)\s+(\d{1,2}\.\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex BoxNumber();
}
