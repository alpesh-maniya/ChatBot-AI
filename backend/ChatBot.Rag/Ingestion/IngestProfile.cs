using System.Text.Json;

namespace ChatBot.Rag.Ingestion;

// Per-document facts the layout heuristics can't work out reliably on their own: which
// pages are covers, where the second page numbering starts, which page is an org chart.
// Lives in a JSON file next to the PDF so a new edition is a new profile, not new code.
//
// A PDF without a profile still ingests: CreateDefault gives it one part, no page rules,
// and a title from the file. That is what uploaded documents get.
public sealed record IngestProfile
{
    public required string DocId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<PartRange> Parts { get; init; }

    /// <summary>File name of the PDF this profile belongs to, when it isn't &lt;pdf name&gt;.profile.json.</summary>
    public string? SourceFile { get; init; }

    /// <summary>Longer name for the system prompt: "the U.S. Government Publishing Office (GPO) 2018 Annual Report".</summary>
    public string? Subject { get; init; }

    /// <summary>Shown as suggestions in an empty chat.</summary>
    public IReadOnlyList<string> ExampleQuestions { get; init; } = [];

    public IReadOnlyList<int> SkipPages { get; init; } = [];
    public IReadOnlyList<int> OrgChartPages { get; init; } = [];
    public IReadOnlyList<int> GlossaryPages { get; init; } = [];

    /// <summary>Pages whose charts have tick labels but no values in the text layer.</summary>
    public IReadOnlyList<int> ChartPages { get; init; } = [];

    /// <summary>Font family the chart labels use. Blocks in it on chart pages are dropped.</summary>
    public string ChartFont { get; init; } = "Calibri";

    /// <summary>
    /// Contract-style layout: clause numbers in their own column, all-caps article
    /// headings. On for documents without a profile; a hand-tuned profile leaves it off
    /// unless the document needs it.
    /// </summary>
    public bool NumberedClauses { get; init; }

    /// <summary>Literal fixes applied after cleaning, for words the hyphen rule can't repair.</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Heading levels read from the fonts, for reports whose sub-headings are set at body
    /// size in another face (the World Bank's "Outlook" is 11pt Helvetica next to 11pt
    /// Garamond), which the size-based rule can't see. Checked before that rule.
    /// </summary>
    public IReadOnlyList<HeadingStyle> HeadingStyles { get; init; } = [];

    /// <summary>
    /// Fonts whose blocks are chart furniture on every page: axis ticks, panel letters,
    /// source lines. On their own they are words without values, and as chunks they win
    /// searches for the region or year they name. <see cref="ChartPages"/> does the same
    /// for one font on listed pages.
    /// </summary>
    public IReadOnlyList<DropStyle> DropStyles { get; init; } = [];

    /// <summary>
    /// Search small, answer from the parent, within a 4k-token prompt: when the best match
    /// and another match share a section, passage [1] becomes the start of that section.
    /// A report's sub-section opens with its headline ("Growth in SAR is expected to soften
    /// to 6.3 percent in 2026"); the chunks after it score as well and say less.
    /// </summary>
    public bool SectionLead { get; init; }

    /// <summary>
    /// Pages of numbered endnotes ("18 Energy consumption and energy efficiency values are
    /// based on..."). When set, superscript markers in the text (a 1-2 digit word set well
    /// below its line's size) are taken out of the sentence and the note's text is added
    /// to the paragraph or table that cites it, so a claim and its caveat are one chunk.
    /// </summary>
    public IReadOnlyList<int> EndnotePages { get; init; } = [];

    /// <summary>Page regions that are one table each; rows are rebuilt from word positions
    /// with every value next to its label. For designed layouts the zone finder splits.</summary>
    public IReadOnlyList<ElementRegion> Tables { get; init; } = [];

    /// <summary>Page regions that are one chart each. Without a vision model only the text
    /// layer is kept, but as one chunk: "445 kg Baseline emissions", "-99 kg Emissions
    /// reductions" and "346 kg iMac emissions" belong together.</summary>
    public IReadOnlyList<ElementRegion> Figures { get; init; } = [];

    /// <summary>
    /// Brochure call-outs: a big figure ("76%") with its caption in smaller type below it
    /// ("recycled content in fiber packaging") become one paragraph.
    /// </summary>
    public bool JoinCallouts { get; init; }

    /// <summary>Question words that name a section; see <see cref="SectionRoute"/>.</summary>
    public IReadOnlyList<SectionRoute> Routes { get; init; } = [];

    public PartRange PartOf(int pdfPage) =>
        Parts.FirstOrDefault(p => pdfPage >= p.FromPage && pdfPage <= p.ToPage)
        ?? throw new InvalidOperationException($"PDF page {pdfPage} is not inside any part in the ingest profile.");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IngestProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<IngestProfile>(stream, Json)
               ?? throw new InvalidDataException($"Ingest profile '{path}' is empty.");
    }

    // One part over the whole file with an empty label, so citations read "p. 12" rather
    // than "Document p. 12".
    public static IngestProfile CreateDefault(string docId, string title, int pageCount) => new()
    {
        DocId = docId,
        Title = title,
        Parts = [new PartRange("Document", "", 1, Math.Max(1, pageCount))],
        NumberedClauses = true
    };

    /// <summary>
    /// The profile for <paramref name="pdfPath"/>: "&lt;name&gt;.profile.json" beside it, or any
    /// *.profile.json in the same folder whose sourceFile names it. Null when there is none.
    /// </summary>
    public static IngestProfile? FindFor(string pdfPath)
    {
        var folder = Path.GetDirectoryName(pdfPath) ?? ".";
        var fileName = Path.GetFileName(pdfPath);

        var sameName = Path.Combine(folder, Path.GetFileNameWithoutExtension(pdfPath) + ".profile.json");
        if (File.Exists(sameName)) return Load(sameName);

        foreach (var candidate in Directory.EnumerateFiles(folder, "*.profile.json"))
        {
            var profile = Load(candidate);
            if (string.Equals(profile.SourceFile, fileName, StringComparison.OrdinalIgnoreCase)) return profile;
        }

        return null;
    }
}

/// <param name="RunningHeaderIsSection">The financial statements print their section
/// ("Section I: Management Discussion and Analysis") at the top of every page. Using it as
/// the top-level heading is more reliable than finding the divider page.</param>
/// <param name="Section">Top-level heading for every page of the part, for chapters whose
/// title is only on a divider page: "Chapter 2: Regional Outlooks".</param>
public sealed record PartRange(
    string Name,
    string Label,
    int FromPage,
    int ToPage,
    bool RunningHeaderIsSection = false,
    string? Section = null)
{
    /// <summary>"Annual Report p. 23", or "p. 4" for a part without a label.</summary>
    public static string PageText(string label, string pages) =>
        string.IsNullOrEmpty(label) ? $"p. {pages}" : $"{label} p. {pages}";
}

/// <summary>
/// A block whose main font contains <paramref name="Font"/>, at a size in
/// [<paramref name="MinSize"/>, <paramref name="MaxSize"/>], and (when given) whose text
/// matches <paramref name="Pattern"/>, is a heading at <paramref name="Level"/>: 2 for a
/// section, 3 and 4 below it. Level 1 is the part's <see cref="PartRange.Section"/>. Level 0
/// marks a style as body text that the bold-heading rule would otherwise take.
/// </summary>
public sealed record HeadingStyle(string Font, double MinSize, double MaxSize, int Level, string? Pattern = null);

/// <summary>Blocks set in exactly <paramref name="Font"/> (subset prefix ignored) at
/// <paramref name="MaxSize"/> or smaller are dropped.</summary>
public sealed record DropStyle(string Font, double MaxSize);

/// <summary>A rectangle on one PDF page, in points from the bottom-left corner, as
/// <c>--fonts --pages</c> prints block positions. A block belongs to it when its centre is
/// inside.</summary>
public sealed record ElementRegion(int Page, double Left, double Bottom, double Right, double Top, string? Title = null)
{
    public bool Contains(Box box) =>
        box.CenterX >= Left && box.CenterX <= Right && box.CenterY >= Bottom && box.CenterY <= Top;
}

/// <summary>
/// When a question contains one of <paramref name="Terms"/> (whole words, any case), search
/// only the chunks under a heading named <paramref name="Section"/>. For names a reader
/// uses that the text may not: "South Asia" for a section that says "SAR", "executive
/// summary" for the pages under that title. Only names, never topics: a filter that guesses
/// wrong hides the answer.
/// </summary>
public sealed record SectionRoute(IReadOnlyList<string> Terms, string Section);
