using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBot.Eval;

public sealed record EvalQuestion
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required IReadOnlyList<string> Turns { get; init; }
    public IReadOnlyList<int> GoldPages { get; init; } = [];
    public IReadOnlyList<string> MustContain { get; init; } = [];
    public bool ExpectFallback { get; init; }

    /// <summary>The document the answer must come from; set from --docs, not the JSON.</summary>
    public string? Doc { get; init; }

    public string Question => Turns[^1];
    public string? Previous => Turns.Count > 1 ? Turns[^2] : null;

    private sealed record File(IReadOnlyList<EvalQuestion> Questions);

    public static IReadOnlyList<EvalQuestion> Load(string path)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), json)!.Questions;
    }
}

/// <summary>One retrieved source as the evaluator sees it, whichever way it was fetched.</summary>
public sealed record SourceHit(int Number, string ChunkId, int PdfPageStart, int PdfPageEnd, float? VectorScore, int? Bm25Rank)
{
    // Chunk ids are "<docId>:<ordinal>".
    public string DocId => ChunkId[..Math.Max(0, ChunkId.LastIndexOf(':'))];
}

public sealed record EvalResult
{
    public required EvalQuestion Question { get; init; }
    public required IReadOnlyList<SourceHit> Sources { get; init; }
    public required float TopVectorScore { get; init; }
    public required bool Grounded { get; init; }
    public double RetrievalMs { get; init; }

    public string? Answer { get; init; }
    public double? TimeToFirstTokenSeconds { get; init; }
    public double? TotalSeconds { get; init; }
    public double? TokensPerSecond { get; init; }
    public IReadOnlyList<int> CitedSources { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>The server searched this question together with the one before it.</summary>
    public bool JoinedToPrevious { get; init; }

    // ---- retrieval -----------------------------------------------------------------

    public int? FirstGoldRank => Question.GoldPages.Count == 0
        ? null
        : Sources.FirstOrDefault(s => (Question.Doc is null || s.DocId == Question.Doc)
                                      && Question.GoldPages.Any(p => p >= s.PdfPageStart && p <= s.PdfPageEnd))?.Number;

    public bool Hit => FirstGoldRank is not null;
    public double ReciprocalRank => FirstGoldRank is { } r ? 1.0 / r : 0;

    // ---- answer --------------------------------------------------------------------

    public IReadOnlyList<string> MissingFacts => Answer is null
        ? Question.MustContain
        : Question.MustContain.Where(f => !Scoring.Contains(Answer, f)).ToArray();

    public bool FactsMatch => Answer is not null && MissingFacts.Count == 0;

    public bool AnsweredWithFallback => Answer is not null && Scoring.IsRefusal(Answer);

    /// <summary>Every [n] in the answer points at a source that was actually sent.</summary>
    public bool CitationsValid => CitedSources.All(n => n >= 1 && n <= Sources.Count);

    /// <summary>Cited passages from a document other than the question's own.</summary>
    public IReadOnlyList<string> ForeignCitations => Question.Doc is null
        ? []
        : CitedSources.Select(n => Sources.FirstOrDefault(s => s.Number == n))
            .Where(s => s is not null && !string.Equals(s.DocId, Question.Doc, StringComparison.OrdinalIgnoreCase))
            .Select(s => s!.ChunkId)
            .ToArray();

    public bool Passed => ForeignCitations.Count == 0 && (Question.ExpectFallback
        ? AnsweredWithFallback
        : FactsMatch && !AnsweredWithFallback);
}

public static partial class Scoring
{
    private static readonly string[] RefusalMarkers =
    [
        "couldn't find", "could not find", "not found in", "does not contain", "doesn't contain", "not mentioned",
        "no information", "does not provide", "doesn't provide", "does not include", "doesn't include",
        "not covered", "does not mention", "doesn't mention", "not in the report", "not available in"
    ];

    public static bool IsRefusal(string answer) =>
        RefusalMarkers.Any(m => answer.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>"a|b" passes if either alternative appears.</summary>
    public static bool Contains(string answer, string expected)
    {
        var haystack = Normalize(answer);
        return expected.Split('|').Any(alt => haystack.Contains(Normalize(alt), StringComparison.Ordinal));
    }

    // Case, "$", thousands separators and spacing don't change a fact.
    public static string Normalize(string text)
    {
        var t = text.ToLowerInvariant().Replace("$", "").Replace("‑", "-");
        // Matches can't overlap, so "874,466,000" needs two passes.
        string previous;
        do
        {
            previous = t;
            t = ThousandsComma().Replace(t, "$1$2");
        } while (t != previous);

        return Spaces().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"(\d),(\d{3})")]
    private static partial Regex ThousandsComma();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
