using System.Diagnostics;
using System.Text;
using ChatBot.Rag.Chunking;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Ingestion;

// Builds RAG indexes from PDFs. Run from the repository root:
//
//   dotnet run --project backend/ChatBot.Ingest              the GPO FY 2018 report
//   dotnet run --project backend/ChatBot.Ingest -- --all     every PDF in data/source
//   dotnet run --project backend/ChatBot.Ingest -- --input some.pdf
//
// A PDF uses <name>.profile.json beside it (or a profile whose sourceFile names it) when
// there is one, and default rules when there isn't. Each index goes to data/index/<docId>.
// Overrides: --profile <json> --output <folder> --embedding-model <gguf> --dump
//
// Tuning a profile: --fonts lists the fonts and sizes (--pages 19,96 for every block on
// those pages), --parse-only writes blocks.txt and chunks.txt without embedding.
// --no-profile ingests with default rules even when a profile exists, for a baseline.
//
// The API does the same on its own for any PDF it finds without a current index, so this
// tool is for bulk runs and for the --dump output: blocks.txt and chunks.txt next to the
// index, which is how you check what the parser made of a PDF before trusting retrieval.

NativeLogging.ErrorsOnly();

var options = IngestArgs.Parse(args);
var total = Stopwatch.StartNew();

if (args.Contains("--fonts"))
{
    // --pages 19,96: every block on those pages with its font, instead of the summary.
    var at = Array.IndexOf(args, "--pages");
    var pages = at >= 0 && at + 1 < args.Length
        ? args[at + 1].Split(',').Select(int.Parse).ToHashSet()
        : null;
    foreach (var input in options.Inputs) Console.WriteLine(pages is null ? FontReport(input) : PageFonts(input, pages));
    return 0;
}

using var embedder = new LlamaEmbeddingModel(new EmbeddingOptions { ModelPath = options.EmbeddingModel });
await embedder.LoadAsync(CancellationToken.None);
if (!embedder.IsReady)
{
    Console.Error.WriteLine($"Embedding model failed to load: {embedder.LoadError}");
    return 1;
}
Console.WriteLine($"Embedder : {embedder.Name} ({embedder.Dimensions} dims)");

var failed = 0;
foreach (var input in options.Inputs)
{
    Console.WriteLine();
    try
    {
        await IngestOne(input);
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
    {
        Console.Error.WriteLine($"FAILED {Path.GetFileName(input)}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine($"Done in {total.Elapsed.TotalSeconds:N1}s{(failed > 0 ? $", {failed} failed" : "")}.");
return failed > 0 ? 1 : 0;

async Task IngestOne(string input)
{
    var sha = await IngestPipeline.Sha256Async(input);
    var profile = options.Profile is not null ? IngestProfile.Load(options.Profile)
        : options.NoProfile ? null
        : IngestProfile.FindFor(input);
    var profileNote = options.Profile ?? (profile is null ? "(none - default rules)" : "found beside the PDF");
    profile ??= IngestPipeline.DefaultProfile(input, sha);
    var output = options.Output ?? Path.Combine(options.IndexRoot, profile.DocId);

    Console.WriteLine($"PDF      : {input}");
    Console.WriteLine($"Profile  : {profileNote}");
    Console.WriteLine($"Doc id   : {profile.DocId}  \"{profile.Title}\"");
    Console.WriteLine($"Output   : {output}");

    if (options.ParseOnly)
    {
        // Steps 1-3 only, no embedding and no index: seconds instead of minutes, for
        // tuning a profile. The dumps go next to where the index would be.
        var pages = new PdfLayoutReader().Read(input, profile.SkipPages.ToHashSet());
        var document = new DocumentAssembler(profile).Assemble(pages);
        var chunks = new SectionChunker(new ChunkingOptions(), embedder).Chunk(document, profile);
        var folder = output + ".parse";
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "blocks.txt"), DumpBlocks(document));
        File.WriteAllText(Path.Combine(folder, "chunks.txt"), DumpChunks(chunks));
        var sizes = chunks.Select(c => c.TokenCount).Order().ToArray();
        Console.WriteLine($"  {chunks.Count} chunks, tokens median {sizes[sizes.Length / 2]} / max {sizes[^1]}; dumps in {folder}");
        foreach (var w in document.Warnings) Console.WriteLine($"  ! {w}");
        return;
    }

    var step = Stopwatch.StartNew();
    var progress = new SyncProgress<IngestProgress>(p =>
    {
        // Embedding reports every 10 chunks; only the last line is worth printing.
        if (p.Stage == IngestStage.Embedding && p.Done != p.Total) return;
        Console.WriteLine($"  {p.Stage,-11} {p.Message}  ({step.Elapsed.TotalSeconds:N1}s)");
    });

    var result = await new IngestPipeline(embedder).RunAsync(input, profile, output, progress);

    var byKind = result.Document.Blocks.GroupBy(b => b.Kind).ToDictionary(g => g.Key, g => g.Count());
    var tokenCounts = result.Chunks.Select(c => c.TokenCount).Order().ToArray();
    Console.WriteLine($"  {result.PagesRead} of {result.Manifest.PdfPageCount} pages; " +
                      string.Join(", ", byKind.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key.ToString().ToLowerInvariant()}")) +
                      $"; {result.Document.Acronyms.Count} acronyms");
    Console.WriteLine($"  {result.Chunks.Count} chunks ({string.Join(", ", result.Chunks.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"))}); " +
                      $"tokens min {tokenCounts.First()} / median {tokenCounts[tokenCounts.Length / 2]} / max {tokenCounts.Last()}; " +
                      $"{result.Elapsed.TotalSeconds:N1}s");

    if (options.Dump)
    {
        File.WriteAllText(Path.Combine(output, "blocks.txt"), DumpBlocks(result.Document));
        File.WriteAllText(Path.Combine(output, "chunks.txt"), DumpChunks(result.Chunks));
        Console.WriteLine("  wrote blocks.txt and chunks.txt for review");
    }

    foreach (var w in result.Document.Warnings) Console.WriteLine($"  ! {w}");
}

static string DumpBlocks(AssembledDocument document)
{
    var sb = new StringBuilder();
    var page = 0;
    foreach (var b in document.Blocks)
    {
        if (b.PdfPage != page)
        {
            page = b.PdfPage;
            sb.AppendLine().AppendLine($"=============== PDF page {page} (printed {b.PrintedPage ?? "-"}) ===============");
        }
        var label = b.Kind == BlockKind.Heading ? $"Heading L{b.HeadingLevel}{(b.IsRunIn ? " run-in" : "")}" : b.Kind.ToString();
        sb.AppendLine($"[{label}] {string.Join(" > ", b.SectionPath)}");
        if (b.Title is not null) sb.AppendLine($"  title: {b.Title}{(b.Unit is null ? "" : $"  ({b.Unit})")}");
        foreach (var line in b.Text.Split('\n')) sb.AppendLine("  " + line);
    }

    sb.AppendLine().AppendLine("=============== Acronyms ===============");
    foreach (var (k, v) in document.Acronyms.OrderBy(a => a.Key)) sb.AppendLine($"{k} = {v}");
    return sb.ToString();
}

static string DumpChunks(IReadOnlyList<DocumentChunk> chunks)
{
    var sb = new StringBuilder();
    foreach (var c in chunks)
    {
        sb.AppendLine($"=============== {c.Id}  {c.Kind}  pdf {c.PdfPageStart}-{c.PdfPageEnd}  {c.TokenCount} tokens");
        sb.AppendLine(c.Header);
        sb.AppendLine(c.Text);
        sb.AppendLine();
    }
    return sb.ToString();
}

// --fonts: every font and size the blocks use, with how much text each carries and a few
// short examples. Body text is the size with the most characters; headings are the rare,
// short ones above it. This is what a profile's heading rules are read from.
static string FontReport(string pdf)
{
    var pages = new PdfLayoutReader().Read(pdf, new HashSet<int>());
    var styles = pages
        .SelectMany(p => p.Blocks.Select(b => (Page: p.Number, Block: b)))
        .GroupBy(x => (Font: BaseFont(x.Block.Font), Size: Math.Round(x.Block.FontSize, 1)))
        .Select(g => new
        {
            g.Key.Font,
            g.Key.Size,
            Chars = g.Sum(x => x.Block.Text.Length),
            Blocks = g.Count(),
            Pages = g.Select(x => x.Page).Distinct().Count(),
            Examples = g.Where(x => x.Block.Text.Length is > 2 and < 70)
                        .Select(x => $"p.{x.Page} \"{x.Block.Text.ReplaceLineEndings(" ")}\"")
                        .Distinct().Take(4).ToList()
        })
        .OrderByDescending(s => s.Chars)
        .ToList();

    // "KLLFER+AGaramondPro-Regular": the prefix names the embedded subset, not the face.
    static string BaseFont(string font) => font.Length > 7 && font[6] == '+' ? font[7..] : font;

    var sb = new StringBuilder($"{Path.GetFileName(pdf)}: {pages.Count} pages, {styles.Count} font/size combinations\n\n");
    sb.AppendLine($"{"chars",8} {"blocks",6} {"pages",5}  {"size",5}  font");
    foreach (var s in styles)
    {
        sb.AppendLine($"{s.Chars,8} {s.Blocks,6} {s.Pages,5}  {s.Size,5:0.0}  {s.Font}");
        foreach (var e in s.Examples) sb.AppendLine($"{"",29}{e}");
    }
    return sb.ToString();
}

static string PageFonts(string pdf, IReadOnlySet<int> wanted)
{
    var sb = new StringBuilder();
    foreach (var page in new PdfLayoutReader().Read(pdf, new HashSet<int>()).Where(p => wanted.Contains(p.Number)))
    {
        sb.AppendLine($"=============== PDF page {page.Number} ({page.Width:0}x{page.Height:0}) ===============");
        foreach (var b in page.Blocks)
        {
            var text = b.Text.ReplaceLineEndings(" ");
            sb.AppendLine($"{b.FontSize,5:0.0}  {b.Font[(b.Font.IndexOf('+') + 1)..],-28} x={b.Box.Left,4:0} y={b.Box.Top,4:0}  {(text.Length > 90 ? text[..90] + "..." : text)}");

            // Words well below the block's size: superscript note markers, mostly.
            foreach (var w in b.Words.Where(w => w.Size < b.FontSize * 0.8))
            {
                sb.AppendLine($"{"",36}small word \"{w.Text}\" {w.Size:0.0}pt at x={w.Box.Left:0} y={w.Box.Top:0}");
            }
        }
    }
    return sb.ToString();
}

// Progress<T> posts to the thread pool, which can print lines out of order; this doesn't.
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed record IngestArgs(
    IReadOnlyList<string> Inputs,
    string? Profile,
    string? Output,
    string IndexRoot,
    string EmbeddingModel,
    bool Dump,
    bool ParseOnly,
    bool NoProfile)
{
    public static IngestArgs Parse(string[] args)
    {
        var root = FindRepoRoot();
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
        }

        var source = Path.Combine(root, "data", "source");
        IReadOnlyList<string> inputs = Get("--input") is { } one
            ? [one]
            : args.Contains("--all")
                ? Directory.EnumerateFiles(source, "*.pdf").Order().ToArray()
                : [Path.Combine(source, "GPO-Anual-report.pdf")];

        if (inputs.Count > 1 && (Get("--profile") is not null || Get("--output") is not null))
        {
            throw new ArgumentException("--profile and --output only make sense with a single --input.");
        }

        return new IngestArgs(
            Inputs: inputs,
            Profile: Get("--profile"),
            Output: Get("--output"),
            IndexRoot: Path.Combine(root, "data", "index"),
            EmbeddingModel: Get("--embedding-model") ?? Path.Combine(root, "models", "bge-small-en-v1.5-q8_0.gguf"),
            Dump: args.Contains("--dump"),
            ParseOnly: args.Contains("--parse-only"),
            NoProfile: args.Contains("--no-profile"));
    }

    // The folder that has both data/ and models/, searched upwards from where we run.
    private static string FindRepoRoot()
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
}
