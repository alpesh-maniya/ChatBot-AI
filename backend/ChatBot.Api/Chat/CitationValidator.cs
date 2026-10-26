using System.Text;
using System.Text.RegularExpressions;
using ChatBot.Rag.Chunking;
using ChatBot.Rag.Index;

namespace ChatBot.Api.Chat;

/// <param name="AutoCited">Sentences that got a citation added here because the model
/// stated a figure without one.</param>
public sealed record CitationCheck(string Text, IReadOnlyList<int> Cited, int UncitedSentences, int RemovedMarkers, int AutoCited);

// Runs once the reply has finished streaming.
//
// 1. A 1.5B model sometimes writes [6] when it was given five sources; that marker is
//    removed rather than left pointing at nothing.
// 2. It also often states a figure with no citation at all. When the figure appears in
//    exactly the words of one of the sources, that source is cited for it. Only numbers
//    are matched: they are what readers check, and they are unambiguous to look up.
public static partial class CitationValidator
{
    public static CitationCheck Check(string reply, IReadOnlyList<string> sourceTexts)
    {
        var removed = 0;

        var text = Marker().Replace(reply, m =>
        {
            var numbers = Numbers(m.Groups[1].Value);
            var valid = numbers.Where(n => n >= 1 && n <= sourceTexts.Count).Distinct().ToList();
            removed += numbers.Count - valid.Count;
            return valid.Count == 0 ? "" : $"[{string.Join(", ", valid)}]";
        });

        // "...in FY 2018. [1]" - the marker after the full stop. Left there, the sentence
        // reads as uncited, gets a marker of its own, and the reply ends "2018 [1]. [1]".
        text = MarkerAfterStop().Replace(text, " $2$1");

        // "...19808 [1]. [1]" - the model cites, ends the sentence, then cites again.
        text = RepeatedMarker().Replace(text, "$1$3");

        // A closing "Sources:\n[1]" of its own; the UI lists the sources under every reply.
        text = TrailingSourceList().Replace(text, "");

        var normalizedSources = sourceTexts.Select(NormalizeNumbers).ToArray();
        var auto = 0;
        var uncited = 0;
        var lines = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            var sentences = SentenceSplitter.Split(line);
            var rebuilt = new List<string>();

            foreach (var sentence in sentences)
            {
                if (Marker().IsMatch(sentence))
                {
                    rebuilt.Add(sentence);
                    continue;
                }

                var source = BestSourceFor(sentence, normalizedSources);
                if (source > 0)
                {
                    rebuilt.Add(AppendMarker(sentence, source));
                    auto++;
                }
                else
                {
                    rebuilt.Add(sentence);
                    if (sentence.Split(' ').Length >= 6 && sentence.Any(char.IsDigit)) uncited++;
                }
            }

            // Keep blank lines and indentation of list items as the model wrote them.
            var indent = line[..(line.Length - line.TrimStart().Length)];
            lines.Add(sentences.Count == 0 ? line : indent + string.Join(' ', rebuilt));
        }

        var result = string.Join('\n', lines);
        var cited = Marker().Matches(result)
            .SelectMany(m => Numbers(m.Groups[1].Value))
            .Distinct()
            .Order()
            .ToArray();

        return new CitationCheck(result, cited, uncited, removed, auto);
    }

    /// <summary>"[2]" or "[1]." - markers and punctuation, no answer. "1,133 [1]" is an answer.</summary>
    public static bool IsOnlyMarkers(string reply) => !Marker().Replace(reply, "").Any(char.IsLetterOrDigit);

    /// <summary>
    /// The sentence in the sources that shares the most content words with the question,
    /// and its source number. For when the model replies with nothing but "[2]": the answer
    /// was in front of it, so quoting it beats both the bare marker and "not found".
    /// Null when no sentence shares at least two words with the question.
    /// </summary>
    public static (string Sentence, int Source)? QuoteBest(string question, IReadOnlyList<string> sourceTexts)
    {
        var wanted = Bm25Index.Tokenize(question).ToHashSet();
        if (wanted.Count == 0) return null;

        (string Sentence, int Source, int Score)? best = null;
        for (var i = 0; i < sourceTexts.Count; i++)
        {
            foreach (var line in sourceTexts[i].Split('\n'))
            foreach (var sentence in SentenceSplitter.Split(line))
            {
                var score = Bm25Index.Tokenize(sentence).Distinct().Count(wanted.Contains);
                if (best is null || score > best.Value.Score || (score == best.Value.Score && sentence.Length < best.Value.Sentence.Length))
                {
                    best = (sentence.Trim(), i + 1, score);
                }
            }
        }

        return best is { Score: >= 2 } b ? (b.Sentence, b.Source) : null;
    }

    // The source (1-based) that contains the most of this sentence's figures; 0 if none.
    // Years are ignored: "2018" is in every source.
    private static int BestSourceFor(string sentence, string[] sources)
    {
        var figures = Figure().Matches(sentence)
            .Select(m => m.Value.Replace(",", "").TrimEnd('.'))
            .Where(f => f.Length >= 2 && !IsYear(f))
            .Distinct()
            .ToList();
        if (figures.Count == 0) return 0;

        var best = 0;
        var bestCount = 0;
        for (var i = 0; i < sources.Length; i++)
        {
            var count = figures.Count(f => ContainsFigure(sources[i], f));
            if (count > bestCount)
            {
                best = i + 1;
                bestCount = count;
            }
        }
        return best;
    }

    private static bool ContainsFigure(string source, string figure)
    {
        var at = source.IndexOf(figure, StringComparison.Ordinal);
        while (at >= 0)
        {
            // Whole number only: "52.6" must not match inside "152.6".
            var before = at == 0 ? ' ' : source[at - 1];
            var afterIndex = at + figure.Length;
            var after = afterIndex >= source.Length ? ' ' : source[afterIndex];
            if (!char.IsDigit(before) && !char.IsDigit(after) && !(after == '.' && afterIndex + 1 < source.Length && char.IsDigit(source[afterIndex + 1])))
            {
                return true;
            }
            at = source.IndexOf(figure, at + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static string AppendMarker(string sentence, int source)
    {
        var trimmed = sentence.TrimEnd();
        var end = trimmed.Length;
        while (end > 0 && ".!?:".Contains(trimmed[end - 1])) end--;
        return new StringBuilder(trimmed[..end]).Append(" [").Append(source).Append(']').Append(trimmed[end..]).ToString();
    }

    private static string NormalizeNumbers(string text) => ThousandsComma().Replace(ThousandsComma().Replace(text, "$1$2"), "$1$2");

    private static bool IsYear(string figure) =>
        figure.Length == 4 && int.TryParse(figure, out var y) && y is >= 1800 and <= 2100;

    private static List<int> Numbers(string list) =>
        list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    [GeneratedRegex(@"\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"(\[(\d+(?:, \d+)*)\])([.!?])\s*\[\2\]")]
    private static partial Regex RepeatedMarker();

    [GeneratedRegex(@"(?<=\w)([.!?])[ \t]*(\[\d+(?:, \d+)*\])")]
    private static partial Regex MarkerAfterStop();

    [GeneratedRegex(@"\s*\n\s*Sources?:\s*(\[\d+(?:, \d+)*\][\s,]*)*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSourceList();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex Figure();

    [GeneratedRegex(@"(\d),(\d{3})")]
    private static partial Regex ThousandsComma();
}
