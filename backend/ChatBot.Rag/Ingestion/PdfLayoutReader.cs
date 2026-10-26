using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ChatBot.Rag.Ingestion;

// The only file that knows about PdfPig.
//
//   letters -> words (nearest neighbour) -> blocks (Docstrum) -> reading order
//
// Docstrum keeps the two text columns of the narrative pages apart, which the plain
// page.Text concatenation does not. Tables come out one block per column; the
// TableStitcher puts the rows back together later.
public sealed class PdfLayoutReader
{
    public int CountPages(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.NumberOfPages;
    }

    /// <summary>The Title from the document information dictionary; often empty or junk.</summary>
    public string? MetadataTitle(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.Information.Title;
    }

    public IReadOnlyList<PageLayout> Read(string path, IReadOnlySet<int> skipPages)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<PageLayout>(document.NumberOfPages);

        for (var n = 1; n <= document.NumberOfPages; n++)
        {
            if (skipPages.Contains(n)) continue;
            pages.Add(ReadPage(document.GetPage(n)));
        }

        return pages;
    }

    private static PageLayout ReadPage(Page page)
    {
        // Spread layouts leave the facing page's text on the page too, positioned off the
        // visible area (PDF page 15 carries all of page 14 at negative x). Only letters
        // inside the page box are real.
        var visible = page.Letters
            .Where(l => l.BoundingBox.Left >= -2 && l.BoundingBox.Right <= page.Width + 2
                        && l.BoundingBox.Bottom >= -2 && l.BoundingBox.Top <= page.Height + 2)
            .ToList();

        var words = NearestNeighbourWordExtractor.Instance.GetWords(visible);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);

        var result = new List<LayoutBlock>();
        foreach (var block in ordered)
        {
            var lines = block.TextLines
                .Select(l => new LayoutLine(ToBox(l.BoundingBox), l.Words.Select(ToWord).ToArray()))
                .Where(l => l.Words.Count > 0)
                .ToArray();

            if (lines.Length > 0)
            {
                result.Add(new LayoutBlock(block.ReadingOrder, ToBox(block.BoundingBox), lines));
            }
        }

        return new PageLayout(page.Number, page.Width, page.Height, result);
    }

    private static LayoutWord ToWord(Word word)
    {
        var size = word.Letters.Count == 0 ? 0 : word.Letters.Average(l => l.PointSize);
        // Word exports set in Calibri use a "ti" ligature glyph with no Unicode mapping, so
        // "Confidential" arrives as "Confiden\0al". It is nearly always "ti".
        var text = word.Text.Contains('\0') ? word.Text.Replace("\0", "ti") : word.Text;
        return new LayoutWord(text, ToBox(word.BoundingBox), word.FontName ?? "", size);
    }

    private static Box ToBox(PdfRectangle r) => new(r.Left, r.Bottom, r.Right, r.Top);
}
