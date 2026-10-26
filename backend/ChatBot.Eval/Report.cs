using System.Globalization;
using System.Text;

namespace ChatBot.Eval;

public static class Report
{
    public static void WriteCsv(string path, IReadOnlyList<EvalResult> results, bool retrievalOnly)
    {
        var sb = new StringBuilder();
        sb.Append("id,type,question,hit,first_gold_rank,top_vector_score,grounded,retrieved_pages,retrieval_ms");
        if (!retrievalOnly) sb.Append(",passed,facts_match,fallback,missing_facts,cited,ttft_s,total_s,tokens_per_s,answer");
        sb.AppendLine();

        foreach (var r in results)
        {
            var cells = new List<string>
            {
                r.Question.Id, r.Question.Type, r.Question.Question, r.Hit.ToString(), r.FirstGoldRank?.ToString() ?? "",
                F(r.TopVectorScore), r.Grounded.ToString(),
                string.Join(" ", r.Sources.Select(s => s.PdfPageStart == s.PdfPageEnd ? $"{s.PdfPageStart}" : $"{s.PdfPageStart}-{s.PdfPageEnd}")),
                F(r.RetrievalMs)
            };

            if (!retrievalOnly)
            {
                cells.AddRange([
                    r.Passed.ToString(), r.FactsMatch.ToString(), r.AnsweredWithFallback.ToString(),
                    string.Join("; ", r.MissingFacts), string.Join(" ", r.CitedSources),
                    F(r.TimeToFirstTokenSeconds), F(r.TotalSeconds), F(r.TokensPerSecond), r.Answer ?? r.Error ?? ""
                ]);
            }

            sb.AppendLine(string.Join(',', cells.Select(Csv)));
        }

        File.WriteAllText(path, sb.ToString());
    }

    public static string Summary(IReadOnlyList<EvalResult> results, bool retrievalOnly, float minScore)
    {
        var answerable = results.Where(r => !r.Question.ExpectFallback && r.Question.GoldPages.Count > 0).ToList();
        var negatives = results.Where(r => r.Question.ExpectFallback).ToList();
        var sb = new StringBuilder();

        sb.AppendLine($"# Eval run {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Questions | {results.Count} ({answerable.Count} answerable, {negatives.Count} negative) |");
        sb.AppendLine($"| Hit@5 | {Pct(answerable.Count(r => r.Hit), answerable.Count)} |");
        sb.AppendLine($"| MRR | {F(answerable.Count == 0 ? 0 : answerable.Average(r => r.ReciprocalRank))} |");
        sb.AppendLine($"| Gate (min vector score {F(minScore)}): answerable passed | {Pct(answerable.Count(r => r.Grounded), answerable.Count)} |");
        sb.AppendLine($"| Gate: negatives stopped | {Pct(negatives.Count(r => !r.Grounded), negatives.Count)} |");
        sb.AppendLine($"| Retrieval ms p50 / p95 | {F(Percentile(results.Select(r => r.RetrievalMs), 50))} / {F(Percentile(results.Select(r => r.RetrievalMs), 95))} |");

        if (!retrievalOnly)
        {
            var withFacts = results.Where(r => r.Question.MustContain.Count > 0).ToList();
            sb.AppendLine($"| Passed overall | {Pct(results.Count(r => r.Passed), results.Count)} |");
            sb.AppendLine($"| Key-fact match | {Pct(withFacts.Count(r => r.FactsMatch), withFacts.Count)} |");
            sb.AppendLine($"| Fallback on negatives | {Pct(negatives.Count(r => r.AnsweredWithFallback), negatives.Count)} |");
            sb.AppendLine($"| False fallback on answerable | {Pct(answerable.Count(r => r.AnsweredWithFallback), answerable.Count)} |");
            sb.AppendLine($"| Answers with at least one citation | {Pct(answerable.Count(r => r.CitedSources.Count > 0), answerable.Count)} |");
            sb.AppendLine($"| Citations pointing at a sent source | {Pct(results.Count(r => r.CitationsValid), results.Count)} |");
            if (results.Any(r => r.Question.Doc is not null))
                sb.AppendLine($"| Answers citing another document | {Pct(results.Count(r => r.ForeignCitations.Count > 0), results.Count)} |");
            sb.AppendLine($"| Searched together with the previous question | {Pct(results.Count(r => r.JoinedToPrevious), results.Count)} |");
            sb.AppendLine($"| Time to first token p50 / p95 (s) | {F(Percentile(results.Select(r => r.TimeToFirstTokenSeconds ?? 0), 50))} / {F(Percentile(results.Select(r => r.TimeToFirstTokenSeconds ?? 0), 95))} |");
            sb.AppendLine($"| Total answer time p50 / p95 (s) | {F(Percentile(results.Select(r => r.TotalSeconds ?? 0), 50))} / {F(Percentile(results.Select(r => r.TotalSeconds ?? 0), 95))} |");
        }

        sb.AppendLine();
        sb.AppendLine("## By question type");
        sb.AppendLine();
        sb.AppendLine(retrievalOnly ? "| Type | n | Hit@5 |" : "| Type | n | Hit@5 | Passed |");
        sb.AppendLine(retrievalOnly ? "| --- | --- | --- |" : "| --- | --- | --- | --- |");
        foreach (var g in results.GroupBy(r => r.Question.Type))
        {
            var withGold = g.Where(r => r.Question.GoldPages.Count > 0).ToList();
            var hit = withGold.Count == 0 ? "-" : Pct(withGold.Count(r => r.Hit), withGold.Count);
            sb.AppendLine(retrievalOnly
                ? $"| {g.Key} | {g.Count()} | {hit} |"
                : $"| {g.Key} | {g.Count()} | {hit} | {Pct(g.Count(r => r.Passed), g.Count())} |");
        }

        // Where to put the gate: the lowest top score among answerable questions against
        // the highest among negatives.
        sb.AppendLine();
        sb.AppendLine("## Gate calibration");
        sb.AppendLine();
        if (answerable.Count > 0)
            sb.AppendLine($"- Lowest top vector score, answerable: {F(answerable.Min(r => r.TopVectorScore))} ({answerable.MinBy(r => r.TopVectorScore)!.Question.Id})");
        if (negatives.Count > 0)
            sb.AppendLine($"- Highest top vector score, negative: {F(negatives.Max(r => r.TopVectorScore))} ({negatives.MaxBy(r => r.TopVectorScore)!.Question.Id})");

        var misses = answerable.Where(r => !r.Hit).ToList();
        if (misses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Retrieval misses");
            sb.AppendLine();
            foreach (var r in misses)
            {
                sb.AppendLine($"- {r.Question.Id} \"{r.Question.Question}\" gold {string.Join(",", r.Question.GoldPages)}, got {string.Join(",", r.Sources.Select(s => s.PdfPageStart))}");
            }
        }

        return sb.ToString();
    }

    private static string Pct(int n, int of) => of == 0 ? "-" : $"{n}/{of} ({100.0 * n / of:F0}%)";

    private static string F(double? v) => v is null ? "" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Percentile(IEnumerable<double> values, int p)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var i = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
