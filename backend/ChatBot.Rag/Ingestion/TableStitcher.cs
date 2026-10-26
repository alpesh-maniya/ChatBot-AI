namespace ChatBot.Rag.Ingestion;

// PdfPig hands a table back one block per column:
//
//   [Fiscal Year]   [Appropriation]   [In Constant Dollars]
//   [1980      ]    [$ 91.6 million]  [$ 285.4 million    ]
//   [1985      ]    [94.0 million  ]  [220.5 million      ]
//
// Rows are rebuilt by grouping lines that share a baseline, then reading each group
// left to right. That is all a table needs to be useful to the model: the label and its
// numbers on one line.
public static class TableStitcher
{
    public static IReadOnlyList<IReadOnlyList<string>> Stitch(IEnumerable<LayoutLine> lines)
    {
        var rows = new List<Row>();

        foreach (var line in lines.OrderByDescending(l => l.Box.CenterY))
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row is not null && row.Accepts(line))
            {
                row.Add(line);
            }
            else
            {
                rows.Add(new Row(line));
            }
        }

        return rows.Select(r => r.Cells()).Where(c => c.Count > 0).ToArray();
    }

    public static string Render(IReadOnlyList<string> cells) => string.Join(" | ", cells);

    private sealed class Row
    {
        private readonly List<LayoutLine> _lines = [];
        private double _centerSum;

        public Row(LayoutLine first) => Add(first);

        private double CenterY => _centerSum / _lines.Count;

        public bool Accepts(LayoutLine line)
        {
            var height = Math.Min(line.Box.Height, _lines.Min(l => l.Box.Height));
            var tolerance = Math.Max(2.5, height * 0.45);
            return Math.Abs(line.Box.CenterY - CenterY) <= tolerance;
        }

        public void Add(LayoutLine line)
        {
            _lines.Add(line);
            _centerSum += line.Box.CenterY;
        }

        public IReadOnlyList<string> Cells()
        {
            var cells = new List<(string Text, Box Box)>();

            foreach (var line in _lines.OrderBy(l => l.Box.Left))
            {
                var text = TextCleaner.Normalize(line.Text).Trim();
                if (text.Length == 0) continue;

                // Two fragments of one visual cell, split by the line extractor.
                if (cells.Count > 0 && line.Box.Left - cells[^1].Box.Right < 4)
                {
                    var prev = cells[^1];
                    cells[^1] = (prev.Text + " " + text, Box.Union([prev.Box, line.Box]));
                    continue;
                }

                cells.Add((text, line.Box));
            }

            // A lone "$" is the currency column of the statements, not a value of its own.
            var result = new List<string>();
            for (var i = 0; i < cells.Count; i++)
            {
                if (cells[i].Text == "$" && i + 1 < cells.Count)
                {
                    result.Add("$" + cells[i + 1].Text);
                    i++;
                }
                else
                {
                    result.Add(cells[i].Text);
                }
            }
            return result;
        }
    }
}
