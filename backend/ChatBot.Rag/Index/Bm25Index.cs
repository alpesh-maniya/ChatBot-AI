using System.Text.RegularExpressions;

namespace ChatBot.Rag.Index;

// Okapi BM25 over the chunk text. Vectors are good at "what is this about"; BM25 is good
// at exact tokens the embedding blurs: "FDLP", "FY 2018", "874,466", "KPMG".
//
// Built in memory at startup; for a few hundred chunks that takes milliseconds, so it
// isn't stored.
public sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "did", "do", "does", "for", "from", "had", "has", "have",
        "how", "in", "is", "it", "its", "of", "on", "or", "that", "the", "their", "this", "to", "was", "were",
        "what", "when", "where", "which", "who", "why", "will", "with", "gpo", "gpo's"
    };

    private readonly List<Dictionary<string, int>> _termFrequencies = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly int[] _lengths;
    private readonly double _averageLength;

    public Bm25Index(IReadOnlyList<string> documents)
    {
        _lengths = new int[documents.Count];

        for (var i = 0; i < documents.Count; i++)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            var tokens = Tokenize(documents[i]);
            foreach (var t in tokens) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) _documentFrequency[t] = _documentFrequency.GetValueOrDefault(t) + 1;

            _termFrequencies.Add(tf);
            _lengths[i] = tokens.Count;
        }

        _averageLength = documents.Count == 0 ? 0 : _lengths.Average();
    }

    /// <param name="only">When given, rows outside it are not scored. IDF still comes from
    /// the whole document, so a word common everywhere stays cheap inside one section.</param>
    public IReadOnlyList<(int Row, double Score)> Search(string query, int k, IReadOnlySet<int>? only = null)
    {
        var terms = Tokenize(query).Distinct().ToList();
        var n = _termFrequencies.Count;
        var results = new List<(int Row, double Score)>();

        for (var row = 0; row < n; row++)
        {
            if (only is not null && !only.Contains(row)) continue;

            double score = 0;
            var tf = _termFrequencies[row];

            foreach (var term in terms)
            {
                if (!tf.TryGetValue(term, out var f)) continue;

                var df = _documentFrequency[term];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * (f * (K1 + 1)) / (f + K1 * (1 - B + B * _lengths[row] / _averageLength));
            }

            if (score > 0) results.Add((row, score));
        }

        return results.OrderByDescending(r => r.Score).Take(k).ToArray();
    }

    // Lower-cased words and numbers. Thousands separators are dropped so "874,466" and
    // "874466" match; decimals keep their point ("874.5").
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in Token().Matches(text.ToLowerInvariant()))
        {
            var t = m.Value.Replace(",", "").Trim('.', '\'');
            if (t.Length == 0 || StopWords.Contains(t)) continue;
            tokens.Add(t);
        }
        return tokens;
    }

    [GeneratedRegex(@"\d[\d,]*(\.\d+)?|[a-z][a-z0-9'&]*")]
    private static partial Regex Token();
}
