using ChatBot.Rag.Chunking;
using ChatBot.Rag.Index;

namespace ChatBot.Rag.Retrieval;

// Cuts a retrieved passage down to the part that answers the question before it goes
// into the prompt.
//
// On CPU the wait before the first word is the model reading the prompt, ~46 tokens/s
// for the 1.5B model, and four 350-token passages are most of that prompt. Usually two
// or three sentences in a passage carry the answer; the rest is context the model reads
// and ignores. Retrieval still ranks whole chunks - only what the model is shown shrinks.
//
//   prose:  the sentences sharing the most words with the question, each with the
//           sentence before it (that is where "it" and "this" get their meaning),
//           in their original order
//   tables: the title and header lines, then the rows that mention the question's words
public static class PassageTrimmer
{
    public static string Trim(string text, string query, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        var wanted = Words(query);
        if (wanted.Count == 0) return Cut(text, maxChars);

        var lines = text.Split('\n');
        return lines.Count(l => l.Contains(" | ")) >= 2
            ? TrimTable(lines, wanted, maxChars)
            : TrimProse(text, wanted, maxChars);
    }

    private static string TrimProse(string text, HashSet<string> wanted, int maxChars)
    {
        var sentences = text.Split('\n').SelectMany(SentenceSplitter.Split).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (sentences.Count <= 2) return Cut(text, maxChars);

        var score = Scorer(sentences, wanted);
        var ranked = sentences
            .Select((s, i) => (Index: i, Score: score(s)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .ToList();
        if (ranked.Count == 0) return Cut(text, maxChars);

        var keep = new SortedSet<int>();
        var length = 0;

        bool TryKeep(int i)
        {
            if (i < 0 || i >= sentences.Count || keep.Contains(i)) return true;
            if (keep.Count > 0 && length + sentences[i].Length + 1 > maxChars) return false;
            keep.Add(i);
            length += sentences[i].Length + 1;
            return true;
        }

        foreach (var (index, _) in ranked)
        {
            if (!TryKeep(index)) break;
            TryKeep(index - 1);
        }

        // Elided spans are marked so the model doesn't read two distant sentences as one
        // thought.
        var parts = new List<string>();
        var previous = -1;
        foreach (var i in keep)
        {
            if (previous >= 0 && i != previous + 1) parts.Add("...");
            parts.Add(sentences[i]);
            previous = i;
        }
        return string.Join(' ', parts);
    }

    private static string TrimTable(string[] lines, HashSet<string> wanted, int maxChars)
    {
        // Everything up to and including the first row with cells is the table's own
        // description: unit note, title, column headings. Without it "874.5 | 874.3" means
        // nothing.
        var header = 0;
        while (header < lines.Length && !lines[header].Contains(" | ")) header++;
        header = Math.Min(lines.Length, header + 1);

        var kept = new List<string>(lines.Take(header));
        var length = kept.Sum(l => l.Length + 1);

        var body = lines.Skip(header).ToList();
        var score = Scorer(body, wanted);
        var rows = body
            .Select((line, i) => (Line: line, Index: i, Score: score(line)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Index)
            .ToList();
        if (rows.Count == 0) return Cut(string.Join('\n', lines), maxChars);

        var chosen = new List<(string Line, int Index)>();
        foreach (var row in rows)
        {
            if (chosen.Count > 0 && length + row.Line.Length + 1 > maxChars) break;
            chosen.Add((row.Line, row.Index));
            length += row.Line.Length + 1;
        }

        kept.AddRange(chosen.OrderBy(r => r.Index).Select(r => r.Line));
        return string.Join('\n', kept);
    }

    // Question words in a sentence, each weighted by how rare it is within this passage:
    // in an annual report "FY 2018" is in nearly every sentence and says nothing about
    // which one answers "total revenues", while "revenue" is in one. A word in every
    // sentence weighs nothing.
    private static Func<string, double> Scorer(IReadOnlyList<string> units, HashSet<string> wanted)
    {
        var words = units.Select(Words).ToList();
        var weight = wanted.ToDictionary(w => w, w =>
        {
            var df = words.Count(u => u.Contains(w));
            return df == 0 ? 0 : Math.Log((double)units.Count / df);
        });
        return unit => Words(unit).Where(wanted.Contains).Sum(w => weight[w]);
    }

    // BM25's tokens with crude suffix folding, applied to question and passage alike, so
    // "revenues" finds "Revenue" and "total" finds "totaled". Wrong stems ("operat") are
    // harmless: both sides get the same one.
    private static HashSet<string> Words(string text) => Bm25Index.Tokenize(text).Select(Fold).ToHashSet();

    private static string Fold(string t)
    {
        if (char.IsDigit(t[0])) return t;
        if (t.Length > 5 && t.EndsWith("ing")) return t[..^3];
        if (t.Length > 4 && t.EndsWith("ed")) return t[..^2];
        if (t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss")) return t[..^1];
        return t;
    }

    // Nothing matched: keep the opening, which is where a chunk's topic sentence is.
    private static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf(' ', maxChars);
        return text[..(cut > maxChars / 2 ? cut : maxChars)] + " ...";
    }
}
