namespace ChatBot.Rag.Ingestion;

// Plain records mirroring what PdfPig gives us. Kept separate from PdfPig's own types so
// the classifier and table stitcher can be tested with hand-built pages.
//
// PDF coordinates: origin bottom-left, so Top > Bottom.
public readonly record struct Box(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    public double VerticalOverlap(Box other) => Math.Min(Top, other.Top) - Math.Max(Bottom, other.Bottom);

    public bool OverlapsHorizontally(Box other, double slack = 0) =>
        Left - slack < other.Right && other.Left - slack < Right;

    public static Box Union(IEnumerable<Box> boxes)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var x in boxes)
        {
            l = Math.Min(l, x.Left);
            b = Math.Min(b, x.Bottom);
            r = Math.Max(r, x.Right);
            t = Math.Max(t, x.Top);
        }
        return new Box(l, b, r, t);
    }
}

public sealed record LayoutWord(string Text, Box Box, string Font, double Size)
{
    public bool IsBold => FontStyle.IsBold(Font);
}

public sealed record LayoutLine(Box Box, IReadOnlyList<LayoutWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

public sealed record LayoutBlock(int Order, Box Box, IReadOnlyList<LayoutLine> Lines)
{
    public string Text => string.Join('\n', Lines.Select(l => l.Text));

    public IEnumerable<LayoutWord> Words => Lines.SelectMany(l => l.Words);

    public double WordsPerLine => Lines.Count == 0 ? 0 : (double)Words.Count() / Lines.Count;

    // Weighted by characters so one big drop-cap doesn't skew a paragraph.
    public double FontSize
    {
        get
        {
            double total = 0, weight = 0;
            foreach (var w in Words)
            {
                total += w.Size * w.Text.Length;
                weight += w.Text.Length;
            }
            return weight == 0 ? 0 : total / weight;
        }
    }

    public string Font =>
        Words.GroupBy(w => w.Font)
             .OrderByDescending(g => g.Sum(w => w.Text.Length))
             .Select(g => g.Key)
             .FirstOrDefault() ?? "";

    public bool IsBold => FontStyle.IsBold(Font);
}

public sealed record PageLayout(int Number, double Width, double Height, IReadOnlyList<LayoutBlock> Blocks);

public static class FontStyle
{
    // HelveticaNeueLTStd-Md, Arial-BoldMT, Arial-Black, Cambria-Bold...
    private static readonly string[] BoldMarkers = ["-Md", "Bold", "-Bd", "Black", "Heavy", "Semibold", "Medium"];

    public static bool IsBold(string font) =>
        BoldMarkers.Any(m => font.Contains(m, StringComparison.OrdinalIgnoreCase));
}
