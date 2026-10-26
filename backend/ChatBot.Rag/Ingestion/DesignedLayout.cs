using System.Text.RegularExpressions;
using ChatBot.Rag.Documents;

namespace ChatBot.Rag.Ingestion;

// Brochure-style pages (Apple's environmental reports): facts in call-outs, small tables
// and charts, caveats in numbered endnotes. Everything here is switched on by profile
// settings, so reports laid out as running text never go through it.
internal static partial class DesignedLayout
{
    // A note marker survives cleaning and table stitching as this word, and the assembler
    // swaps it for the note's text once every page, endnotes included, has been read.
    public static string Token(int note) => $"⟦n{note}⟧";

    [GeneratedRegex(@"\s*⟦n(\d{1,2})⟧")]
    public static partial Regex TokenPattern();

    // ---- note markers ------------------------------------------------------------------

    // "ENERGY 18 STAR": the 18 is 8pt inside 11.9pt text. A 1-2 digit word under three
    // quarters of its block's size is a superscript; a block that is nothing but one is a
    // marker the layout set apart from its sentence, and goes onto the line it sits on.
    public static List<LayoutBlock> MarkNotes(List<LayoutBlock> blocks)
    {
        var result = blocks.Select(MarkInline).ToList();

        foreach (var marker in result.Where(IsMarkerBlock).ToList())
        {
            var host = result
                .Where(b => !ReferenceEquals(b, marker) && !IsMarkerBlock(b))
                .Where(b => b.Box.VerticalOverlap(marker.Box) > 0 && b.Box.Left <= marker.Box.Left)
                .OrderBy(b => Math.Max(0, marker.Box.Left - b.Box.Right))
                .FirstOrDefault()
                ?? result
                    .Where(b => !ReferenceEquals(b, marker) && !IsMarkerBlock(b))
                    .Where(b => b.Box.OverlapsHorizontally(marker.Box, 20) && b.Box.Bottom >= marker.Box.Bottom - 2)
                    .OrderBy(b => b.Box.Bottom - marker.Box.Top)
                    .FirstOrDefault();
            if (host is null) continue;

            var at = result.IndexOf(host);
            result[at] = Attach(host, marker);
            result.Remove(marker);
        }

        return result;
    }

    private static LayoutBlock MarkInline(LayoutBlock block)
    {
        var size = block.FontSize;
        if (IsMarkerBlock(block)) return block;

        var lines = block.Lines
            .Select(l => l with { Words = l.Words.Select(w => IsMarker(w, size) ? w with { Text = Token(int.Parse(w.Text)) } : w).ToArray() })
            .ToArray();
        return block with { Lines = lines };
    }

    private static bool IsMarker(LayoutWord word, double blockSize) =>
        word.Text.Length is 1 or 2 && word.Text.All(char.IsDigit) && word.Text[0] != '0' && word.Size < blockSize * 0.75;

    private static bool IsMarkerBlock(LayoutBlock block)
    {
        var text = block.Text.Trim();
        return block.FontSize <= 6.5 && text.Length is 1 or 2 && text.All(char.IsDigit) && text[0] != '0';
    }

    private static LayoutBlock Attach(LayoutBlock host, LayoutBlock marker)
    {
        var token = new LayoutWord(Token(int.Parse(marker.Text.Trim())), marker.Box, marker.Words.First().Font, marker.FontSize);
        var line = host.Lines
            .Select((l, i) => (i, Overlap: l.Box.VerticalOverlap(marker.Box)))
            .OrderByDescending(x => x.Overlap)
            .First().i;

        var lines = host.Lines.ToArray();
        lines[line] = lines[line] with { Words = [.. lines[line].Words, token] };
        return host with { Lines = lines };
    }

    // ---- endnotes ----------------------------------------------------------------------

    // "18" in 4.7pt at the left of the column, the note's text beside it at the same height,
    // and any blocks below (note 18 has a bulleted list) until the next number.
    public static (List<LayoutBlock> Others, List<(int Number, string Text)> Notes) Endnotes(
        List<LayoutBlock> blocks, TextCleaner cleaner)
    {
        var rest = new List<LayoutBlock>();
        var notes = new List<(int Number, List<string> Parts)>();

        foreach (var block in blocks.OrderByDescending(b => Math.Round(b.Box.Top)).ThenBy(b => b.Box.Left))
        {
            if (IsMarkerBlock(block))
            {
                notes.Add((int.Parse(block.Text.Trim()), []));
                continue;
            }

            // The page title ("Endnotes") and anything above the first number stay as text.
            if (notes.Count == 0 || block.FontSize > 9)
            {
                rest.Add(block);
                continue;
            }

            notes[^1].Parts.Add(cleaner.JoinLines(block.Lines.Select(l => l.Text)));
        }

        return (rest, notes.Select(n => (n.Number, string.Join(' ', n.Parts))).ToList());
    }

    // ---- call-outs ---------------------------------------------------------------------

    // "76%" in 17.5pt over "recycled content in fiber packaging" in 9.5pt.
    public static List<LayoutBlock> JoinCallouts(List<LayoutBlock> blocks)
    {
        var result = new List<LayoutBlock>(blocks);
        for (var i = 0; i < result.Count; i++)
        {
            var head = result[i];
            if (!Figure().IsMatch(head.Text.Trim())) continue;

            while (true)
            {
                var current = result[i];
                var gap = head.FontSize * 1.5;
                var next = result.FindIndex(b =>
                    !ReferenceEquals(b, current)
                    && b.FontSize < head.FontSize * 0.75
                    && Math.Abs(b.Box.Left - head.Box.Left) < 4
                    && b.Box.Top <= current.Box.Bottom + 2
                    && b.Box.Top >= current.Box.Bottom - gap);
                if (next < 0) break;

                result[i] = new LayoutBlock(current.Order, Box.Union([current.Box, result[next].Box]), [.. current.Lines, .. result[next].Lines]);
                result.RemoveAt(next);
                if (next < i) i--;
            }
        }
        return result;
    }

    // "100%", "76%", "346 kg", "<1%"
    [GeneratedRegex(@"^[<>~]?\d[\d.,]*\s?(%|kg|g|percent)?$")]
    private static partial Regex Figure();

    // ---- tables and charts -------------------------------------------------------------

    public static SourceBlock Table(IReadOnlyList<LayoutBlock> members, ElementRegion region, PageLayout page, PartRange part)
    {
        var rows = TableStitcher.Stitch(members.SelectMany(m => m.Lines))
            .Select(TableStitcher.Render)
            .Where(r => r.Any(char.IsLetterOrDigit))
            .ToArray();

        return new SourceBlock
        {
            Kind = BlockKind.Table, Text = string.Join('\n', rows), Rows = rows, Title = region.Title,
            PdfPage = page.Number, Part = part.Name
        };
    }

    // Chart labels top to bottom, each once: a legend repeats "Transportation" per bar.
    public static SourceBlock Figure(IReadOnlyList<LayoutBlock> members, ElementRegion region, PageLayout page, PartRange part, TextCleaner cleaner)
    {
        var labels = members
            .OrderByDescending(b => Math.Round(b.Box.Top))
            .ThenBy(b => b.Box.Left)
            .Select(b => cleaner.JoinLines(b.Lines.Select(l => l.Text)))
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Distinct()
            .ToArray();

        return new SourceBlock
        {
            Kind = BlockKind.Table, Text = string.Join('\n', labels), Rows = labels, Title = region.Title ?? "Chart",
            PdfPage = page.Number, Part = part.Name
        };
    }
}
