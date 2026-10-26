using ChatBot.Rag.Documents;

namespace ChatBot.Rag.Ingestion;

public sealed record AssembledDocument(
    IReadOnlyList<SourceBlock> Blocks,
    IReadOnlyDictionary<string, string> Acronyms,
    IReadOnlyDictionary<int, string> PrintedPages,
    IReadOnlyList<string> Warnings);

// Runs the classifier over every page and then does the cross-page work: heading levels,
// section paths, printed page numbers and the acronym map.
public sealed partial class DocumentAssembler(IngestProfile profile)
{
    public AssembledDocument Assemble(IReadOnlyList<PageLayout> pages)
    {
        var cleaner = TextCleaner.FromPages(pages, profile.Replacements);
        var classifier = new PageClassifier(profile, cleaner);
        var bodySizes = BodySizes(pages);
        var warnings = new List<string>();

        var classified = new List<(PageLayout Page, PartRange Part, PageClassifier.PageResult Result)>();
        foreach (var page in pages)
        {
            var part = profile.PartOf(page.Number);
            var result = classifier.Classify(page, part, bodySizes.GetValueOrDefault(part.Name, 10));
            classified.Add((page, part, result));

            if (result.Blocks.Count == 0)
            {
                warnings.Add($"PDF page {page.Number}: no text kept (scanned, image-only or everything filtered).");
            }
        }

        var levelSize = HeadingSizes(classified.SelectMany(c => c.Result.Blocks));
        var blocks = new List<SourceBlock>();
        var printed = new Dictionary<int, string>();
        // Part or chapter > section > sub-section > the level below that.
        var path = new string?[4];
        string? currentPart = null;

        void Set(int level, string text)
        {
            path[level - 1] = text;
            for (var i = level; i < path.Length; i++) path[i] = null;
        }

        foreach (var (page, part, result) in classified)
        {
            if (result.PrintedPage is not null) printed[page.Number] = result.PrintedPage;

            if (part.Name != currentPart)
            {
                Array.Clear(path);
                currentPart = part.Name;
                if (part.Section is not null) Set(1, part.Section);
            }

            if (part.RunningHeaderIsSection && result.RunningHeader is not null && result.RunningHeader != path[0])
            {
                Set(1, result.RunningHeader);
            }

            foreach (var block in result.Blocks)
            {
                var current = block;
                if (block.Kind == BlockKind.Heading)
                {
                    // "5 Smarter chemistry": the note belongs to the claims below, not the title.
                    var title = block.Text.Contains('⟦') ? DesignedLayout.TokenPattern().Replace(block.Text, "").Trim() : block.Text;

                    // A clause heading comes with its level from the numbering ("2." vs "2.1"),
                    // a profile heading style with the level the profile gives it.
                    var level = block.HeadingLevel > 0
                        ? block.HeadingLevel
                        : block.IsRunIn || block.FontSize < levelSize.GetValueOrDefault(part.Name) - 0.25 ? 3 : 2;
                    Set(level, title);
                    current = block with { HeadingLevel = level, Text = title };
                }

                blocks.Add(current with { SectionPath = path.Where(p => p is not null).Cast<string>().ToArray() });
            }
        }

        if (profile.EndnotePages.Count > 0) blocks = AttachNotes(blocks, warnings);

        foreach (var table in blocks.Where(b => b.Kind == BlockKind.Table && b.Rows.Count < 2))
        {
            warnings.Add($"PDF page {table.PdfPage}: table '{table.Title ?? table.Text}' has fewer than 2 rows.");
        }

        return new AssembledDocument(blocks, AcronymExtractor.Extract(blocks), printed, warnings);
    }

    // Footnotes travel with the claim: "iMac uses 58 percent less energy than the
    // requirement for ENERGY STAR. (Note 18: Energy consumption and energy efficiency values
    // are based on the ENERGY STAR Program Requirements for Computers...)". The endnote
    // pages come last, so this waits until every page is read.
    private static List<SourceBlock> AttachNotes(List<SourceBlock> blocks, List<string> warnings)
    {
        var notes = blocks.Where(b => b.NoteNumber is not null)
            .GroupBy(b => b.NoteNumber!.Value)
            .ToDictionary(g => g.Key, g => g.First().Text);
        var pattern = DesignedLayout.TokenPattern();

        return blocks.Select(block =>
        {
            var cited = pattern.Matches(block.Text).Select(m => int.Parse(m.Groups[1].Value))
                .Concat(block.Rows.SelectMany(r => pattern.Matches(r).Select(m => int.Parse(m.Groups[1].Value))))
                .Distinct()
                .ToList();
            if (cited.Count == 0) return block;

            foreach (var n in cited.Where(n => !notes.ContainsKey(n)))
            {
                warnings.Add($"PDF page {block.PdfPage}: note {n} is cited but not on an endnote page.");
            }

            var rows = block.Rows.Select(r => pattern.Replace(r, "")).ToList();
            if (block.Kind == BlockKind.Table)
            {
                var attached = cited.Where(notes.ContainsKey).Select(n => notes[n]).ToList();
                return block with { Text = string.Join('\n', [.. rows, .. attached]), Rows = [.. rows, .. attached] };
            }

            return block with { Text = InsertNotes(block.Text, notes), Rows = rows };
        }).ToList();
    }

    // Each note right after the sentence that cites it, not at the end of the block: a block
    // can stop mid-sentence where the column breaks. Last marker first, so the positions of
    // the earlier ones don't move.
    private static string InsertNotes(string text, IReadOnlyDictionary<int, string> notes)
    {
        var matches = DesignedLayout.TokenPattern().Matches(text).ToList();
        var first = matches.GroupBy(m => m.Groups[1].Value).Select(g => g.First()).ToHashSet();

        foreach (var m in Enumerable.Reverse(matches))
        {
            text = text.Remove(m.Index, m.Length);
            if (!first.Contains(m) || !notes.TryGetValue(int.Parse(m.Groups[1].Value), out var note)) continue;

            var end = SentenceEnd().Match(text, Math.Min(m.Index, text.Length));
            var at = end.Success ? end.Index : text.Length;
            text = text.Insert(at, " (" + note + ")");
        }
        return text;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[.!?])(?=\s|$)")]
    private static partial System.Text.RegularExpressions.Regex SentenceEnd();

    // The most common text size in each part, weighted by characters. The narrative is set
    // around 9.7pt, the financial statements at 12pt, so one global number would not do.
    private Dictionary<string, double> BodySizes(IReadOnlyList<PageLayout> pages)
    {
        var sizes = new Dictionary<string, double>();
        foreach (var group in pages.GroupBy(p => profile.PartOf(p.Number).Name))
        {
            var histogram = group
                .SelectMany(p => p.Blocks)
                .Where(b => b.Lines.Count >= 3)
                .SelectMany(b => b.Words)
                .GroupBy(w => Math.Round(w.Size * 2) / 2)
                .Select(g => (Size: g.Key, Weight: g.Sum(w => w.Text.Length)))
                .OrderByDescending(x => x.Weight)
                .FirstOrDefault();

            if (histogram.Weight > 0) sizes[group.Key] = histogram.Size;
        }
        return sizes;
    }

    // Level-2 heading size per part: the largest size that is used for headings at least
    // three times. A one-off big title ("GPO By the Numbers") still counts as level 2;
    // anything smaller than the level-2 size is a sub-heading.
    private static Dictionary<string, double> HeadingSizes(IEnumerable<SourceBlock> blocks)
    {
        var result = new Dictionary<string, double>();
        foreach (var part in blocks.Where(b => b.Kind == BlockKind.Heading && !b.IsRunIn).GroupBy(b => b.Part))
        {
            var bySize = part.GroupBy(b => Math.Round(b.FontSize * 2) / 2).ToList();
            var frequent = bySize.Where(g => g.Count() >= 3).Select(g => g.Key).ToList();
            result[part.Key] = frequent.Count > 0 ? frequent.Max() : bySize.Max(g => g.Key);
        }
        return result;
    }
}
