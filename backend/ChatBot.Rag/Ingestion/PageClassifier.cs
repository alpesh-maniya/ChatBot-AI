using System.Text.RegularExpressions;
using ChatBot.Rag.Documents;

namespace ChatBot.Rag.Ingestion;

// One page of layout blocks in, cleaned SourceBlocks out, in reading order.
//
// Order of decisions matters:
//   1. drop furniture - footers, page numbers, chart tick labels, letter-spaced titles
//   2. special pages from the profile (org chart, glossary)
//   3. find table zones before anything else, because table cells are short bold-ish
//      lines that would otherwise look like headings
//   4. what's left is heading, caption or paragraph
public sealed partial class PageClassifier(IngestProfile profile, TextCleaner cleaner)
{
    // Footers sit below this line on every page of both parts (narrative ~41pt,
    // financial ~72-83pt). Body text and captions start above it.
    private const double FooterBand = 90;
    private const double HeaderBandFromTop = 45;

    public sealed record PageResult(IReadOnlyList<SourceBlock> Blocks, string? PrintedPage, string? RunningHeader);

    public PageResult Classify(PageLayout page, PartRange part, double bodySize)
    {
        string? printed = null;
        string? running = null;
        var kept = new List<LayoutBlock>();

        foreach (var block in page.Blocks.OrderBy(b => b.Order))
        {
            var text = block.Text.Trim();
            if (text.Length == 0) continue;

            if (block.Box.Top < FooterBand && (block.FontSize <= 8.1 || IsNumeric(text)))
            {
                // With endnotes on, a 5.7pt digit down here is a note marker, not the page number.
                var marker = profile.EndnotePages.Count > 0 && block.FontSize <= 6.5;
                if (printed is null && !marker && block.Lines.Count == 1 && text.Length <= 4 && text.All(char.IsDigit))
                {
                    printed = text;
                }
                continue;
            }

            if (block.Box.Top > page.Height - HeaderBandFromTop && block.Lines.Count == 1)
            {
                running = cleaner.JoinLines([text]);
                continue;
            }

            if (profile.ChartPages.Contains(page.Number)
                && block.Font.Contains(profile.ChartFont, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsLetterSpaced(block)) continue;

            if (profile.DropStyles.Count > 0 && IsDropped(block)) continue;

            kept.Add(block);
        }

        if (profile.HeadingStyles.Count > 0) kept = JoinWrappedHeadings(kept);

        // Designed layouts, all off unless the profile asks. Markers first, so a marker in a
        // table cell is carried into the table; endnotes after the page's own tables are out.
        var elements = new List<SourceBlock>();
        var endnotePage = profile.EndnotePages.Contains(page.Number);
        if (profile.EndnotePages.Count > 0 && !endnotePage) kept = DesignedLayout.MarkNotes(kept);

        foreach (var region in profile.Tables.Concat(profile.Figures).Where(r => r.Page == page.Number))
        {
            var members = kept.Where(b => region.Contains(b.Box)).ToList();
            if (members.Count == 0) continue;
            kept.RemoveAll(members.Contains);
            elements.Add(profile.Figures.Contains(region)
                ? DesignedLayout.Figure(members, region, page, part, cleaner)
                : DesignedLayout.Table(members, region, page, part));
        }

        if (endnotePage)
        {
            (kept, var notes) = DesignedLayout.Endnotes(kept, cleaner);
            elements.AddRange(notes.Select(n => new SourceBlock
            {
                Kind = BlockKind.Paragraph, Text = $"Note {n.Number}: {n.Text}", PdfPage = page.Number, Part = part.Name,
                NoteNumber = n.Number
            }));
        }

        if (profile.JoinCallouts) kept = DesignedLayout.JoinCallouts(kept);

        IReadOnlyList<SourceBlock> blocks;
        if (profile.OrgChartPages.Contains(page.Number))
        {
            blocks = [OrgChart(kept, page, part)];
        }
        else if (profile.GlossaryPages.Contains(page.Number))
        {
            blocks = [Glossary(kept, page, part)];
        }
        else
        {
            blocks = Flow(kept, page, part, bodySize);
        }

        // Tables, charts and endnotes go after the page's text, under its last heading: the
        // reading order puts a designed page's title anywhere, so their own position says little.
        if (elements.Count > 0) blocks = [.. blocks, .. elements];

        return new PageResult(blocks.Select(b => b with { PrintedPage = printed }).ToArray(), printed, running);
    }

    private List<SourceBlock> Flow(List<LayoutBlock> blocks, PageLayout page, PartRange part, double bodySize)
    {
        var zones = FindTableZones(blocks, page);
        var zoneOf = new Dictionary<LayoutBlock, TableZone>(ReferenceEqualityComparer.Instance);
        foreach (var zone in zones)
        {
            foreach (var m in zone.Members) zoneOf[m] = zone;
            if (zone.Title is not null) zoneOf[zone.Title] = zone;
        }

        // Reading order tends to put tables after everything else on the page. Pin each one
        // behind the block printed directly above it instead, so the table lands in the
        // section that introduces it ("15. Major Customers", not "16. Subsequent Events").
        var anchors = new Dictionary<LayoutBlock, List<TableZone>>(ReferenceEqualityComparer.Instance);
        foreach (var zone in zones)
        {
            var box = zone.Title is null ? zone.Box : Box.Union([zone.Box, zone.Title.Box]);
            var anchor = blocks
                .Where(b => !zoneOf.ContainsKey(b) && b.Box.Bottom >= box.Top - 2 && b.Box.OverlapsHorizontally(box))
                .OrderBy(b => b.Box.Bottom)
                .FirstOrDefault();

            if (anchor is null) continue;
            if (!anchors.TryGetValue(anchor, out var list)) anchors[anchor] = list = [];
            list.Add(zone);
        }
        var pinned = anchors.Values.SelectMany(z => z).ToHashSet();

        var emitted = new HashSet<TableZone>();
        var result = new List<SourceBlock>();

        foreach (var block in blocks)
        {
            if (zoneOf.TryGetValue(block, out var zone))
            {
                if (!pinned.Contains(zone) && emitted.Add(zone)) result.AddRange(Emit(zone, page, part));
                continue;
            }

            result.AddRange(Prose(block, page, part, bodySize));

            if (anchors.TryGetValue(block, out var tables))
            {
                foreach (var t in tables.Where(emitted.Add)) result.AddRange(Emit(t, page, part));
            }
        }

        return result;
    }

    private IEnumerable<SourceBlock> Emit(TableZone zone, PageLayout page, PartRange part) =>
        zone.NumberedList ? NumberedList(zone, page, part) : [Table(zone, page, part)];

    // A contract's clause numbers sit in their own column, so "2.1.1 | The business of the
    // Fund..." looks like a table row. Put the number back in front of its text and treat
    // each clause as a paragraph; an all-caps clause ("2. GENERAL PROVISIONS") is the
    // article heading.
    private IEnumerable<SourceBlock> NumberedList(TableZone zone, PageLayout page, PartRange part)
    {
        var items = new List<List<string>>();
        foreach (var cells in TableStitcher.Stitch(zone.Members.SelectMany(m => m.Lines)))
        {
            if (items.Count == 0 || IsOutlineNumber(cells[0])) items.Add([]);
            items[^1].Add(string.Join(' ', cells));
        }

        foreach (var item in items)
        {
            var text = cleaner.JoinLines(item);
            if (!text.Any(char.IsLetter)) continue;

            var number = text.Split(' ', 2)[0];
            var rest = text.Length > number.Length ? text[(number.Length + 1)..] : "";

            if (IsOutlineNumber(number) && IsCapsHeading(rest))
            {
                yield return new SourceBlock
                {
                    Kind = BlockKind.Heading, Text = text, PdfPage = page.Number, Part = part.Name,
                    HeadingLevel = number.TrimEnd('.').Contains('.') ? 3 : 2
                };
                continue;
            }

            yield return new SourceBlock { Kind = BlockKind.Paragraph, Text = text, PdfPage = page.Number, Part = part.Name };
        }
    }

    // ---- prose -------------------------------------------------------------------------

    private IEnumerable<SourceBlock> Prose(LayoutBlock block, PageLayout page, PartRange part, double bodySize)
    {
        var text = cleaner.JoinLines(block.Lines.Select(l => l.Text));
        if (!text.Any(char.IsLetter)) yield break;

        if (StyleOf(block, text) is { } style)
        {
            // Level 0: big bold type that is a statement, not a title.
            if (style.Level == 0)
            {
                yield return new SourceBlock { Kind = BlockKind.Paragraph, Text = text, PdfPage = page.Number, Part = part.Name };
                yield break;
            }

            yield return new SourceBlock
            {
                Kind = BlockKind.Heading, Text = Continued().Replace(text, ""), PdfPage = page.Number, Part = part.Name,
                FontSize = block.FontSize, HeadingLevel = style.Level
            };
            yield break;
        }

        if (IsHeading(block, text, bodySize)
            || (profile.NumberedClauses && block.Lines.Count <= 2 && IsCapsHeading(StripOutlineNumber(text))))
        {
            yield return new SourceBlock
            {
                Kind = BlockKind.Heading, Text = text, PdfPage = page.Number, Part = part.Name, FontSize = block.FontSize
            };
            yield break;
        }

        if (block.FontSize <= bodySize * 0.9 && !block.IsBold)
        {
            yield return new SourceBlock { Kind = BlockKind.Caption, Text = text, PdfPage = page.Number, Part = part.Name };
            yield break;
        }

        // Run-in heading: "FY 2018 Financial Results Revenue totaled $874.5 million..."
        // with the first few words set in the bold sans face.
        var words = block.Words.ToList();
        var lead = 0;
        while (lead < words.Count && words[lead].IsBold && words[lead].Size >= bodySize * 0.95) lead++;

        if (lead > 0 && lead < words.Count && lead <= 12 && !block.IsBold)
        {
            var heading = cleaner.JoinLines([string.Join(' ', words.Take(lead).Select(w => w.Text))]);
            if (heading.Any(char.IsLetter))
            {
                yield return new SourceBlock
                {
                    Kind = BlockKind.Heading, Text = heading.TrimEnd(':', '.'), PdfPage = page.Number, Part = part.Name,
                    IsRunIn = true, FontSize = block.FontSize
                };
                text = cleaner.JoinLines(RemainderLines(block, lead));
            }
        }

        yield return new SourceBlock { Kind = BlockKind.Paragraph, Text = text, PdfPage = page.Number, Part = part.Name };
    }

    private static IEnumerable<string> RemainderLines(LayoutBlock block, int skipWords)
    {
        foreach (var line in block.Lines)
        {
            if (skipWords >= line.Words.Count)
            {
                skipWords -= line.Words.Count;
                continue;
            }
            yield return string.Join(' ', line.Words.Skip(skipWords).Select(w => w.Text));
            skipWords = 0;
        }
    }

    private bool IsDropped(LayoutBlock block)
    {
        var font = block.Font;
        if (font.Length > 7 && font[6] == '+') font = font[7..];
        var size = block.FontSize;
        return profile.DropStyles.Any(d => string.Equals(d.Font, font, StringComparison.OrdinalIgnoreCase) && size <= d.MaxSize + 0.05);
    }

    private HeadingStyle? StyleOf(LayoutBlock block, string text)
    {
        if (profile.HeadingStyles.Count == 0 || block.Lines.Count > 3) return null;
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 16 || text.EndsWith('.')) return null;

        var font = block.Font;
        var size = block.FontSize;
        return profile.HeadingStyles.FirstOrDefault(s =>
            font.Contains(s.Font, StringComparison.OrdinalIgnoreCase)
            && size >= s.MinSize - 0.05 && size <= s.MaxSize + 0.05
            && (s.Pattern is null || Regex.IsMatch(text, s.Pattern)));
    }

    // A long heading wraps into one block per line: "Key trends in EMDE" above "government
    // debt and interest rates". Same face, same left edge, next line down: one heading.
    private List<LayoutBlock> JoinWrappedHeadings(List<LayoutBlock> blocks)
    {
        var result = new List<LayoutBlock>(blocks);
        for (var i = 0; i < result.Count; i++)
        {
            if (StyleOf(result[i], result[i].Text.ReplaceLineEndings(" ")) is null) continue;

            while (true)
            {
                var head = result[i];
                var next = result.FindIndex(b =>
                    !ReferenceEquals(b, head)
                    && b.Font == head.Font
                    && Math.Abs(b.FontSize - head.FontSize) < 0.3
                    && Math.Abs(b.Box.Left - head.Box.Left) < 3
                    && b.Box.Top <= head.Box.Bottom + 2
                    && b.Box.Top >= head.Box.Bottom - head.FontSize * 1.6);
                if (next < 0) break;

                result[i] = new LayoutBlock(head.Order, Box.Union([head.Box, result[next].Box]), [.. head.Lines, .. result[next].Lines]);
                result.RemoveAt(next);
                if (next < i) i--;
            }
        }
        return result;
    }

    private static bool IsHeading(LayoutBlock block, string text, double bodySize)
    {
        if (block.Lines.Count > 2 || !block.IsBold || block.FontSize < bodySize * 0.95) return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 14) return false;
        if (text.EndsWith('.') || text.EndsWith(',') || text.EndsWith(':')) return false;

        // Memo labels on the Inspector General's letter ("Date", "To", "From").
        if (words.Length == 1 && words[0].Length <= 4) return false;

        return true;
    }

    // ---- tables ------------------------------------------------------------------------

    private sealed class TableZone
    {
        public List<LayoutBlock> Members { get; } = [];
        public LayoutBlock? Title { get; set; }
        public bool NumberedList { get; set; }
        public Box Box => Box.Union(Members.Select(m => m.Box));
    }

    private List<TableZone> FindTableZones(List<LayoutBlock> blocks, PageLayout page)
    {
        // Two-column only if narrow prose actually sits on both sides of the gutter. One
        // narrow paragraph on an otherwise full-width page must not split its tables.
        var narrow = blocks.Where(b => IsProse(b) && b.Box.Width < page.Width * 0.55).ToList();
        var twoColumn = narrow.Any(b => b.Box.CenterX < page.Width / 2) && narrow.Any(b => b.Box.CenterX >= page.Width / 2);
        bool SameColumn(LayoutBlock a, LayoutBlock b) =>
            !twoColumn || (a.Box.CenterX < page.Width / 2) == (b.Box.CenterX < page.Width / 2);

        // Seeds: blocks sitting side by side where at least one is a column of numbers.
        // Two prose columns never qualify, which is what keeps them out.
        var parent = new Dictionary<LayoutBlock, LayoutBlock>(ReferenceEqualityComparer.Instance);
        foreach (var b in blocks) parent[b] = b;
        LayoutBlock Find(LayoutBlock x) => ReferenceEquals(parent[x], x) ? x : parent[x] = Find(parent[x]);

        for (var i = 0; i < blocks.Count; i++)
        for (var j = i + 1; j < blocks.Count; j++)
        {
            var a = blocks[i];
            var b = blocks[j];
            if (!SameColumn(a, b)) continue;
            if (!IsNumericColumn(a) && !IsNumericColumn(b)) continue;
            // Long row labels ("Cash flows used in (provided by) investing activities")
            // look like prose by word count; lines that sit exactly beside a column of
            // numbers are table rows whatever their length.
            if ((IsProse(a) || IsProse(b)) && !LinesAlign(a, b)) continue;

            var overlap = a.Box.VerticalOverlap(b.Box);
            if (overlap > 0.5 * Math.Min(a.Box.Height, b.Box.Height))
            {
                parent[Find(a)] = Find(b);
            }
        }

        var zones = blocks.GroupBy(Find, ReferenceEqualityComparer.Instance)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var z = new TableZone();
                z.Members.AddRange(g);
                return z;
            })
            .ToList();

        // Rows of a statement are often separate one-line blocks; merge zones that stack
        // closely on top of each other.
        bool merged;
        do
        {
            merged = false;
            for (var i = 0; i < zones.Count && !merged; i++)
            for (var j = i + 1; j < zones.Count && !merged; j++)
            {
                var a = zones[i].Box;
                var b = zones[j].Box;
                // 60pt spans two label-only rows ("Fund Balances:", "Revolving fund:")
                // between numeric rows. A paragraph in the gap means two separate tables.
                var gap = -a.VerticalOverlap(b);
                var upper = a.Top > b.Top ? a : b;
                var lower = a.Top > b.Top ? b : a;
                var proseBetween = blocks.Any(x => x.WordsPerLine >= 8
                                                   && x.Box.CenterY < upper.Bottom && x.Box.CenterY > lower.Top
                                                   && x.Box.OverlapsHorizontally(a));
                if (gap <= 60 && !proseBetween && a.OverlapsHorizontally(b, 40) && SameColumn(zones[i].Members[0], zones[j].Members[0]))
                {
                    zones[i].Members.AddRange(zones[j].Members);
                    zones.RemoveAt(j);
                    merged = true;
                }
            }
        } while (merged);

        var claimed = new HashSet<LayoutBlock>(zones.SelectMany(z => z.Members), ReferenceEqualityComparer.Instance);

        foreach (var zone in zones)
        {
            var box = zone.Box;

            // Label-only rows inside the table ("Fund Balances:", "Restricted:").
            foreach (var block in blocks)
            {
                if (claimed.Contains(block) || IsProse(block) || block.Lines.Count > 3) continue;
                if (!SameColumn(block, zone.Members[0])) continue;
                if (block.Box.CenterY < box.Bottom - 3 || block.Box.CenterY > box.Top + 3) continue;
                if (!block.Box.OverlapsHorizontally(box, 60)) continue;

                zone.Members.Add(block);
                claimed.Add(block);
            }

            // A clause list has no title; the paragraph above it is just the clause before.
            if (profile.NumberedClauses && IsNumberedList(zone))
            {
                zone.NumberedList = true;
                continue;
            }

            // Title: the nearest short block just above the table.
            box = zone.Box;
            zone.Title = blocks
                .Where(b => !claimed.Contains(b) && b.Box.Bottom >= box.Top - 4 && b.Box.Bottom - box.Top <= 60)
                .Where(b => b.Box.OverlapsHorizontally(box) && b.Lines.Count <= 6)
                .Where(b => b.WordsPerLine < 12 || b.Text.Contains("thousand", StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Box.Bottom - box.Top)
                .FirstOrDefault();

            if (zone.Title is not null) claimed.Add(zone.Title);
        }

        return zones;
    }

    private SourceBlock Table(TableZone zone, PageLayout page, PartRange part)
    {
        var rows = TableStitcher.Stitch(zone.Members.SelectMany(m => m.Lines))
            .Select(TableStitcher.Render)
            .ToArray();

        var title = zone.Title is null ? null : cleaner.JoinLines(zone.Title.Lines.Select(l => l.Text));
        var probe = string.Join(' ', new[] { title ?? "" }.Concat(rows.Take(2)));
        var unit = probe.Contains("thousand", StringComparison.OrdinalIgnoreCase) ? "in thousands" : null;

        return new SourceBlock
        {
            Kind = BlockKind.Table,
            Text = string.Join('\n', rows),
            Rows = rows,
            Title = title,
            Unit = unit,
            PdfPage = page.Number,
            Part = part.Name
        };
    }

    private SourceBlock Glossary(List<LayoutBlock> blocks, PageLayout page, PartRange part)
    {
        var title = blocks.FirstOrDefault(b => b.Lines.Count == 1 && b.IsBold && b.Text.Any(char.IsLetter));
        var rows = TableStitcher.Stitch(blocks.Where(b => !ReferenceEquals(b, title)).SelectMany(b => b.Lines))
            .Where(r => r.Count >= 2)
            .Select(TableStitcher.Render)
            .ToArray();

        return new SourceBlock
        {
            Kind = BlockKind.Glossary,
            Text = string.Join('\n', rows),
            Rows = rows,
            Title = title is null ? "Abbreviations" : cleaner.JoinLines([title.Text]),
            PdfPage = page.Number,
            Part = part.Name
        };
    }

    // Each box of the chart is one block: "CHIEF FINANCIAL OFFICER / William L. Boesch, Jr."
    // Upper-case lines are the office, the rest is the person.
    private SourceBlock OrgChart(List<LayoutBlock> blocks, PageLayout page, PartRange part)
    {
        var title = blocks.FirstOrDefault(b => b.FontSize >= 8 && b.Text.Any(char.IsLower));
        var rows = new List<string>();

        foreach (var block in blocks)
        {
            if (ReferenceEquals(block, title) || block.FontSize >= 8) continue;

            var lines = block.Lines.Select(l => TextCleaner.Normalize(l.Text).Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Any(l => l.Contains("www.", StringComparison.OrdinalIgnoreCase) || l.Contains('|'))) continue;

            var office = string.Join(' ', lines.Where(IsUpper));
            var person = string.Join(' ', lines.Where(l => !IsUpper(l)));
            if (office.Length == 0) continue;

            rows.Add(person.Length == 0 ? office : $"{office}: {person}");
        }

        return new SourceBlock
        {
            Kind = BlockKind.OrgChart,
            Text = string.Join('\n', rows),
            Rows = rows,
            Title = title is null ? "Organizational Chart" : cleaner.JoinLines([title.Text]),
            PdfPage = page.Number,
            Part = part.Name
        };

        static bool IsUpper(string s) => s.Any(char.IsLetter) && !s.Any(char.IsLower);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static bool IsProse(LayoutBlock b) => b.Lines.Count >= 3 && b.WordsPerLine >= 7;

    // Every number in the zone is a clause number and at least one has a dot ("2.1", "3.").
    // A real table has a value column ("$1,200", "15.4%", "2018") that fails this.
    private static bool IsNumberedList(TableZone zone)
    {
        var numbers = zone.Members.Where(IsNumericColumn).SelectMany(b => b.Lines).Select(l => l.Text.Trim())
            .Where(t => NumericValue().IsMatch(t)).ToList();
        if (numbers.Count == 0 || !numbers.All(IsOutlineNumber) || !numbers.Any(n => n.Contains('.'))) return false;

        return zone.Members.Any(b => !IsNumericColumn(b) && b.Text.Any(char.IsLetter));
    }

    private static bool IsOutlineNumber(string text) => OutlineNumber().IsMatch(text.Trim());

    private static string StripOutlineNumber(string text)
    {
        var first = text.Split(' ', 2);
        return first.Length == 2 && IsOutlineNumber(first[0]) ? first[1] : text;
    }

    // "GENERAL PROVISIONS", "SCHEDULE 2: INVESTMENT POLICY". Not "AND", not a shouted sentence.
    private static bool IsCapsHeading(string text)
    {
        var letters = text.Count(char.IsLetter);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return letters >= 4 && words <= 12 && !text.Any(char.IsLower) && !text.EndsWith(',') && !text.EndsWith(';');
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3})*\.?$")]
    private static partial Regex OutlineNumber();

    // "BOX 1.1 How much will AI affect global growth? (continued)" on the box's later pages.
    [GeneratedRegex(@"\s*\(continued\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Continued();

    private static bool LinesAlign(LayoutBlock a, LayoutBlock b) =>
        a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p => Math.Abs(p.First.Box.CenterY - p.Second.Box.CenterY) <= 3);

    private static bool IsNumericColumn(LayoutBlock b)
    {
        var numeric = b.Lines.Count(l => IsNumeric(l.Text));
        return numeric > 0 && numeric >= 0.6 * b.Lines.Count;
    }

    private static bool IsNumeric(string text) => NumericValue().IsMatch(text.Trim());

    // Running headers set in spaced capitals come out as "U. S. GOV E R NM E NT".
    private static bool IsLetterSpaced(LayoutBlock b)
    {
        var words = b.Words.ToList();
        if (words.Count < 4) return false;
        return words.Count(w => w.Text.Length <= 2) >= 0.6 * words.Count;
    }

    // "$ 91.6 million", "(28,745)", "1,737", "2018", "Over 10,000", "15.4%"
    [GeneratedRegex(@"^(over |nearly |about )?[$(]?\s?-?[\d][\d,.]*\)?%?( (million|billion|thousand))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericValue();
}
