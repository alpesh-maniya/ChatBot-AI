using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBot.Eval;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Ingestion;
using ChatBot.Rag.Retrieval;

// Scores the RAG pipeline against data/eval/*.questions.json. Run from the repo root.
//
//   dotnet run --project backend/ChatBot.Eval -- --retrieval-only
//       In-process, no API, no chat model. Seconds. For tuning chunking and the gate.
//
//   dotnet run --project backend/ChatBot.Eval
//       Full answers through the running API (default http://localhost:5093), exactly as
//       the browser gets them. Minutes on CPU.
//
// Options: --questions <json> --index <folder> --api <url> --out <folder> --min-score <0.55>
//          --sources       (retrieval only) print every retrieved chunk with its scores
//          --max-sources 12, --passage-max-chars 700   (retrieval only) as in Rag:Retrieval
//          --no-routes, --no-section-lead   (retrieval only) switch a profile technique off
//          --doc <docId>   (API mode) the document each session is attached to, so other
//                          documents in the library cannot change the score
//          --docs a,b,c    (API mode) attach all of them; --questions then takes one file per
//                          document, in the same order, and an answer citing a passage from
//                          another document fails
//          --one-session   (API mode) every question in one conversation, in file order,
//                          instead of a fresh one each. Run it beside the normal mode: a
//                          question that passes alone and fails here picked up an earlier one

NativeLogging.ErrorsOnly();

var root = FindRepoRoot();
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var questionsPath = Arg("--questions", Path.Combine(root, "data", "eval", "gpo-ar-fy2018.questions.json"));
var docId = Arg("--doc", "gpo-ar-fy2018");
var indexPath = Arg("--index", Path.Combine(root, "data", "index", docId));
var apiUrl = Arg("--api", "http://localhost:5093");
var outRoot = Arg("--out", Path.Combine(root, "eval", "results"));
var retrievalOnly = args.Contains("--retrieval-only");
var showSources = args.Contains("--sources");
var minScore = float.Parse(Arg("--min-score", "0.55"), CultureInfo.InvariantCulture);

var oneSession = args.Contains("--one-session");
var docIds = Arg("--docs", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var questionFiles = questionsPath.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (docIds.Length > 0 && docIds.Length != questionFiles.Length)
{
    Console.Error.WriteLine($"--docs names {docIds.Length} document(s) but --questions has {questionFiles.Length} file(s).");
    return 1;
}

var questions = questionFiles
    .SelectMany((file, i) => EvalQuestion.Load(file).Select(q => docIds.Length > 0 ? q with { Doc = docIds[i] } : q))
    .ToList();
Console.WriteLine($"{questions.Count} questions from {string.Join(", ", questionFiles.Select(Path.GetFileName))}");
Console.WriteLine(retrievalOnly
    ? $"Mode: retrieval only, index {indexPath}"
    : $"Mode: full answers via {apiUrl}, {(docIds.Length > 0 ? string.Join(" + ", docIds) : docId)}, {(oneSession ? "one session for all" : "fresh session each")}");
Console.WriteLine();

var indexPaths = docIds.Length > 0 ? docIds.Select(d => Path.Combine(root, "data", "index", d)).ToArray() : [indexPath];
var results = retrievalOnly
    ? await RetrievalOnly(args, questions, indexPaths, minScore, showSources, int.Parse(Arg("--max-sources", "4")),
        int.Parse(Arg("--passage-max-chars", "0")), float.Parse(Arg("--document-margin", "0"), CultureInfo.InvariantCulture))
    : await ThroughApi(questions, apiUrl, docIds.Length > 0 ? docIds : [docId], oneSession);

var outDir = Path.Combine(outRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + (retrievalOnly ? "-retrieval" : ""));
Directory.CreateDirectory(outDir);
Report.WriteCsv(Path.Combine(outDir, "results.csv"), results, retrievalOnly);
var summary = Report.Summary(results, retrievalOnly, minScore);
File.WriteAllText(Path.Combine(outDir, "summary.md"), summary);

Console.WriteLine();
Console.WriteLine(summary);
Console.WriteLine($"Written to {outDir}");
return 0;

// ---------------------------------------------------------------------------------------

static async Task<IReadOnlyList<EvalResult>> RetrievalOnly(string[] args, IReadOnlyList<EvalQuestion> questions, string[] indexPaths, float minScore,
    bool showSources, int maxSources, int passageMaxChars, float documentMargin)
{
    // The profile beside the source PDF, as the API loads it: routes and SectionLead edits
    // apply without re-indexing.
    var profiles = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "data", "source"), "*.profile.json")
        .Select(IngestProfile.Load)
        .ToArray();
    var indexes = indexPaths.Select(path =>
    {
        var docId = Path.GetFileName(path.TrimEnd('/', '\\'));
        var profile = profiles.FirstOrDefault(p => p.DocId == docId);
        if (profile is not null && args.Contains("--no-routes")) profile = profile with { Routes = [] };
        if (profile is not null && args.Contains("--no-section-lead")) profile = profile with { SectionLead = false };
        return RagIndex.Load(path, profile);
    }).ToArray();

    var modelPath = Path.Combine(FindRepoRoot(), "models", indexes[0].Manifest.EmbeddingModel);
    using var embedder = new LlamaEmbeddingModel(new EmbeddingOptions { ModelPath = modelPath });
    await embedder.LoadAsync(CancellationToken.None);
    if (!embedder.IsReady) throw new InvalidOperationException(embedder.LoadError);

    var retriever = new HybridRetriever(indexes, embedder, new RetrievalOptions
    {
        MinVectorScore = minScore,
        MaxSources = maxSources,
        SourceTokenBudget = maxSources > 4 ? 100_000 : 1400,
        PassageMaxChars = passageMaxChars,
        DocumentMargin = documentMargin
    });
    var results = new List<EvalResult>();

    foreach (var q in questions)
    {
        var r = await retriever.RetrieveAsync(q.Question, q.Previous, CancellationToken.None);
        var result = new EvalResult
        {
            Question = q,
            Sources = r.Sources.Select(s => new SourceHit(s.Number, s.Chunk.Id, s.Chunk.PdfPageStart, s.Chunk.PdfPageEnd, s.VectorScore, s.Bm25Rank)).ToArray(),
            TopVectorScore = r.TopVectorScore,
            Grounded = r.Grounded,
            RetrievalMs = r.ElapsedMilliseconds
        };
        results.Add(result);

        var mark = q.ExpectFallback ? (r.Grounded ? "LEAK" : "ok  ") : (result.Hit ? "hit " : "MISS");
        Console.WriteLine($"{mark} {q.Id,-4} top={r.TopVectorScore:F3} rank={result.FirstGoldRank?.ToString() ?? "-"} " +
                          $"pages=[{string.Join(",", result.Sources.Select(s => s.PdfPageStart))}]  {q.Question}" +
                          (r.Sections is { Count: > 0 } routed ? $"  -> {string.Join("; ", routed)}" : ""));
        if (r.DocumentScores.Count > 1)
        {
            Console.WriteLine("       " + string.Join("  ", r.DocumentScores.Select(d =>
                $"{(d.DocId == q.Doc ? "*" : "")}{d.DocId}={d.Best:F3}{(d.Searched ? "" : " (skipped)")}")));
        }

        if (showSources)
        {
            foreach (var s in r.Sources)
            {
                var text = s.Chunk.Text.ReplaceLineEndings(" ");
                Console.WriteLine($"       [{s.Number}] {s.Chunk.Id} p.{s.Chunk.PdfPageStart} cos={s.VectorScore?.ToString("F3") ?? "-"} " +
                                  $"vrank={s.VectorRank?.ToString() ?? "-"} bm25={s.Bm25Rank?.ToString() ?? "-"}  {text[..Math.Min(100, text.Length)]}");
            }
        }
    }

    return results;
}

static async Task<IReadOnlyList<EvalResult>> ThroughApi(IReadOnlyList<EvalQuestion> questions, string apiUrl, string[] docIds, bool oneSession)
{
    using var http = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromMinutes(10) };
    var results = new List<EvalResult>();
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    var status = await http.GetFromJsonAsync<JsonElement>("/api/model/status");
    if (!status.GetProperty("ready").GetBoolean() || !status.GetProperty("indexReady").GetBoolean())
    {
        throw new InvalidOperationException($"API is not ready: {status}");
    }

    async Task<string> NewSession() =>
        (await (await http.PostAsJsonAsync("/api/sessions", new { documentIds = docIds })).Content.ReadFromJsonAsync<JsonElement>(json))
            .GetProperty("id").GetString()!;

    // Same rule the server uses to decide a message continues the previous question.
    var queries = new QueryBuilder(new Dictionary<string, string>());
    var shared = oneSession ? await NewSession() : null;
    string? lastMessage = null;

    foreach (var q in questions)
    {
        // Normally a fresh session per question, so earlier questions can't leak into later ones.
        var sessionId = shared ?? await NewSession();
        if (shared is null) lastMessage = null;

        TurnResult turn = default!;
        var joined = false;
        foreach (var message in q.Turns)
        {
            joined = queries.Build(message, lastMessage).UsedPreviousTurn;
            turn = await SendTurn(http, sessionId, message);
            lastMessage = message;
        }

        var result = new EvalResult
        {
            Question = q,
            Sources = turn.Sources,
            TopVectorScore = turn.Sources.Select(s => s.VectorScore ?? 0).DefaultIfEmpty(0).Max(),
            Grounded = turn.Grounded,
            Answer = turn.Answer,
            TimeToFirstTokenSeconds = turn.FirstToken,
            TotalSeconds = turn.Total,
            TokensPerSecond = turn.TokensPerSecond,
            CitedSources = turn.Cited,
            Error = turn.Error,
            JoinedToPrevious = joined && q.Turns.Count == 1
        };
        results.Add(result);

        if (shared is null) await http.DeleteAsync($"/api/sessions/{sessionId}");

        var from = string.Join(",", turn.Cited.Select(n => turn.Sources.FirstOrDefault(s => s.Number == n)?.DocId).Distinct());
        Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {q.Id,-4} {turn.Total,5:F1}s ttft={turn.FirstToken,4:F1}s  {q.Question}" +
                          (q.Doc is not null ? $"  cites: {(from.Length > 0 ? from : "-")}" : "") +
                          (result.JoinedToPrevious ? "  [searched with previous question]" : ""));
        if (!result.Passed)
        {
            var missing = result.MissingFacts.Count > 0 ? $" missing: {string.Join("; ", result.MissingFacts)}" : "";
            var foreign = result.ForeignCitations.Count > 0 ? $" cites another document: {string.Join(", ", result.ForeignCitations)}" : "";
            Console.WriteLine($"       {Shorten(turn.Answer ?? turn.Error ?? "(no answer)", 220)}{missing}{foreign}");
        }
    }

    if (shared is not null) await http.DeleteAsync($"/api/sessions/{shared}");
    return results;
}

static async Task<TurnResult> SendTurn(HttpClient http, string sessionId, string message)
{
    var watch = Stopwatch.StartNew();
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/messages")
    {
        Content = JsonContent.Create(new { message })
    };
    request.Headers.Accept.ParseAdd("text/event-stream");

    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
    {
        return new TurnResult([], false, null, 0, 0, null, [], $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);

    var answer = new StringBuilder();
    var sources = new List<SourceHit>();
    var grounded = false;
    double? firstToken = null;
    double? rate = null;
    IReadOnlyList<int> cited = [];
    string? error = null;
    string? eventName = null;

    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("event:")) { eventName = line[6..].Trim(); continue; }
        if (!line.StartsWith("data:")) continue;

        var data = JsonDocument.Parse(line[5..].Trim()).RootElement;
        switch (eventName)
        {
            case "sources":
                grounded = data.GetProperty("grounded").GetBoolean();
                foreach (var s in data.GetProperty("sources").EnumerateArray())
                {
                    sources.Add(new SourceHit(
                        s.GetProperty("n").GetInt32(),
                        s.GetProperty("chunkId").GetString()!,
                        s.GetProperty("pdfPageStart").GetInt32(),
                        s.GetProperty("pdfPageEnd").GetInt32(),
                        s.TryGetProperty("vectorScore", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null,
                        s.TryGetProperty("bm25Rank", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null));
                }
                break;
            case "chunk":
                firstToken ??= watch.Elapsed.TotalSeconds;
                answer.Append(data.GetProperty("text").GetString());
                break;
            case "done":
                rate = data.GetProperty("tokensPerSecond").GetDouble();
                // Score what the user ends up seeing: the server's stored reply, after the
                // citation checks, replaces the streamed text in the UI.
                if (data.TryGetProperty("content", out var stored) && stored.GetString() is { Length: > 0 } final)
                {
                    answer.Clear().Append(final);
                }
                if (data.TryGetProperty("citedSources", out var c))
                {
                    cited = c.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                }
                break;
            case "error":
                error = data.GetProperty("message").GetString();
                break;
        }
    }

    return new TurnResult(sources, grounded, answer.ToString(), firstToken ?? 0, watch.Elapsed.TotalSeconds, rate, cited, error);
}

static string Shorten(string s, int max)
{
    s = s.ReplaceLineEndings(" ");
    return s.Length <= max ? s : s[..max] + "...";
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "data")) && Directory.Exists(Path.Combine(dir.FullName, "models")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

internal sealed record TurnResult(
    IReadOnlyList<SourceHit> Sources,
    bool Grounded,
    string? Answer,
    double FirstToken,
    double Total,
    double? TokensPerSecond,
    IReadOnlyList<int> Cited,
    string? Error);
