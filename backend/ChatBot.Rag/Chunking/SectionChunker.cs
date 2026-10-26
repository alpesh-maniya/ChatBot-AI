using System.Security.Cryptography;
using System.Text;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Ingestion;

namespace ChatBot.Rag.Chunking;

// Packs sentences into ~TargetTokens chunks inside a section, with OverlapTokens of the
// previous chunk carried into the next. A section heading always closes the current
// chunk; a sub-heading only does if the chunk is already worth keeping on its own.
//
// Tables, the org chart and the glossary never share a chunk with prose. A table split
// across chunks repeats its title and header row, so every piece still says what its
// numbers are.
public sealed class SectionChunker(ChunkingOptions options, ITextTokenizer tokenizer)
{
    private sealed record Piece(string Text, int Page, int Tokens);

    private sealed class Pending
    {
        public required ChunkKind Kind { get; init; }
        public required string Part { get; init; }
        public required IReadOnlyList<string> Path { get; init; }
        public string? Unit { get; init; }
        public List<Piece> Pieces { get; } = [];
        public int Tokens => Pieces.Sum(p => p.Tokens);
    }

    public IReadOnlyList<DocumentChunk> Chunk(AssembledDocument document, IngestProfile profile)
    {
        var pending = new List<Pending>();
        var buffer = new List<Piece>();
        var captions = new List<Piece>();
        var fresh = 0;
        IReadOnlyList<string> path = [];
        string? part = null;

        void Emit(bool keepOverlap)
        {
            if (buffer.Count == 0 || fresh == 0)
            {
                buffer.Clear();
                fresh = 0;
                return;
            }

            var chunk = new Pending { Kind = ChunkKind.Text, Part = part!, Path = path };
            chunk.Pieces.AddRange(buffer);
            pending.Add(chunk);

            buffer.Clear();
            fresh = 0;

            if (!keepOverlap) return;

            var carried = 0;
            for (var i = chunk.Pieces.Count - 1; i > 0; i--)
            {
                var piece = chunk.Pieces[i];
                if (carried + piece.Tokens > options.OverlapTokens) break;
                buffer.Insert(0, piece);
                carried += piece.Tokens;
            }
        }

        void Add(Piece piece)
        {
            foreach (var p in SplitOversized(piece))
            {
                if (buffer.Count > 0 && buffer.Sum(b => b.Tokens) + p.Tokens > options.TargetTokens)
                {
                    Emit(keepOverlap: true);
                }
                buffer.Add(p);
                fresh += p.Tokens;
            }
        }

        void CloseSection()
        {
            foreach (var caption in captions) Add(caption);
            captions.Clear();
            Emit(keepOverlap: false);
        }

        foreach (var block in document.Blocks)
        {
            if (block.Part != part)
            {
                if (part is not null) CloseSection();
                part = block.Part;
                path = block.SectionPath;
            }

            switch (block.Kind)
            {
                case BlockKind.Heading:
                    var current = buffer.Sum(b => b.Tokens);
                    if (block.HeadingLevel <= 2 || current >= options.MinTokens)
                    {
                        CloseSection();
                        path = block.SectionPath;
                    }
                    else if (fresh > 0)
                    {
                        // Too little text to stand alone; keep going in the same chunk but
                        // keep the sub-heading's words, they are often what a query matches.
                        Add(Measure(block.Text + ".", block.PdfPage));
                    }
                    else
                    {
                        path = block.SectionPath;
                    }
                    break;

                case BlockKind.Paragraph:
                    if (buffer.Count == 0) path = block.SectionPath;
                    foreach (var sentence in SentenceSplitter.Split(block.Text))
                    {
                        Add(Measure(sentence, block.PdfPage));
                    }
                    break;

                case BlockKind.Caption:
                    captions.Add(Measure(block.Text, block.PdfPage));
                    break;

                case BlockKind.Table:
                case BlockKind.OrgChart:
                case BlockKind.Glossary:
                    pending.AddRange(TableChunks(block));
                    break;
            }
        }

        CloseSection();
        MergeSmall(pending);

        return Finish(pending, document, profile);
    }

    private IEnumerable<Pending> TableChunks(SourceBlock block)
    {
        var kind = block.Kind switch
        {
            BlockKind.OrgChart => ChunkKind.OrgChart,
            BlockKind.Glossary => ChunkKind.Glossary,
            _ => ChunkKind.Table
        };

        // The org chart and glossary are stand-alone pages; whatever heading came before
        // them says nothing about their content.
        // A table's title is already the first line of its text; repeating a long
        // statement title in the path only costs prompt tokens.
        IReadOnlyList<string> path = kind != ChunkKind.Table
            ? [block.Title ?? kind.ToString()]
            : block.SectionPath;
        var rows = block.Rows.Count > 0 ? block.Rows : block.Text.Split('\n');
        var lead = new List<string>();
        if (block.Title is not null) lead.Add(block.Title);
        var headerRow = kind == ChunkKind.Table && rows.Count > 1 ? rows[0] : null;

        Pending Start() => new() { Kind = kind, Part = block.Part, Path = path, Unit = block.Unit };

        var chunk = Start();
        foreach (var l in lead) chunk.Pieces.Add(Measure(l, block.PdfPage));

        for (var i = 0; i < rows.Count; i++)
        {
            var row = Measure(rows[i], block.PdfPage);
            if (chunk.Tokens + row.Tokens > options.TableMaxTokens && chunk.Pieces.Count > lead.Count + 1)
            {
                yield return chunk;
                chunk = Start();
                foreach (var l in lead) chunk.Pieces.Add(Measure(l, block.PdfPage));
                if (headerRow is not null && i > 0) chunk.Pieces.Add(Measure(headerRow, block.PdfPage));
            }
            chunk.Pieces.Add(row);
        }

        if (chunk.Pieces.Count > lead.Count) yield return chunk;
    }

    // A short trailing chunk is folded into the one before it when they share the part
    // and the section heading, and the result still fits.
    private void MergeSmall(List<Pending> pending)
    {
        for (var i = pending.Count - 1; i > 0; i--)
        {
            var small = pending[i];
            var prev = pending[i - 1];
            if (small.Kind != ChunkKind.Text || prev.Kind != ChunkKind.Text) continue;
            if (small.Tokens >= options.MinTokens) continue;
            if (small.Part != prev.Part || !SameSection(small.Path, prev.Path)) continue;
            if (prev.Tokens + small.Tokens > options.MaxTokens) continue;

            prev.Pieces.AddRange(small.Pieces.Where(p => !prev.Pieces.Contains(p)));
            pending.RemoveAt(i);
        }
    }

    private static bool SameSection(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Take(2).SequenceEqual(b.Take(2));

    private IReadOnlyList<DocumentChunk> Finish(List<Pending> pending, AssembledDocument document, IngestProfile profile)
    {
        var chunks = new List<DocumentChunk>(pending.Count);

        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var partRange = profile.Parts.First(x => x.Name == p.Part);
            var first = p.Pieces.Min(x => x.Page);
            var last = p.Pieces.Max(x => x.Page);
            var printed = PrintedRange(document.PrintedPages, first, last);

            var location = printed is null ? $"PDF p. {first}" : PartRange.PageText(partRange.Label, printed);
            var header = p.Path.Count == 0
                ? $"{profile.Title} | {location}"
                : $"{profile.Title} | {string.Join(" > ", p.Path)} | {location}";
            if (p.Unit is not null) header += $" | amounts {p.Unit}";

            var separator = p.Kind == ChunkKind.Text ? " " : "\n";
            var text = string.Join(separator, p.Pieces.Select(x => x.Text));
            var indexText = header + "\n" + text;

            chunks.Add(new DocumentChunk
            {
                Id = $"{profile.DocId}:{i:D4}",
                DocId = profile.DocId,
                Ordinal = i,
                Kind = p.Kind,
                Part = p.Part,
                PartLabel = partRange.Label,
                SectionPath = p.Path,
                PdfPageStart = first,
                PdfPageEnd = last,
                PrintedPages = printed,
                Unit = p.Unit,
                Header = header,
                Text = text,
                TokenCount = tokenizer.CountTokens(indexText),
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indexText))).ToLowerInvariant()
            });
        }

        return chunks;
    }

    private static string? PrintedRange(IReadOnlyDictionary<int, string> printed, int first, int last)
    {
        var a = printed.GetValueOrDefault(first);
        var b = printed.GetValueOrDefault(last);
        if (a is null && b is null) return null;
        if (a is null || b is null || a == b) return a ?? b;
        return $"{a}-{b}";
    }

    private Piece Measure(string text, int page) => new(text, page, tokenizer.CountTokens(text));

    private static readonly string[] Separators = ["; ", ", ", " "];

    // A "sentence" longer than MaxTokens is almost always a list run together. Split on
    // semicolons, then commas, then plain words. A part that is still too long after one
    // separator goes on to the next: the World Bank's appendix notes have one "; " and
    // 500 tokens after it.
    private IEnumerable<Piece> SplitOversized(Piece piece, int from = 0)
    {
        if (piece.Tokens <= options.MaxTokens)
        {
            yield return piece;
            yield break;
        }

        for (var s = from; s < Separators.Length; s++)
        {
            var separator = Separators[s];
            var parts = piece.Text.Split(separator);
            if (parts.Length < 2) continue;

            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                var candidate = sb.Length == 0 ? part : sb + separator + part;
                if (sb.Length > 0 && tokenizer.CountTokens(candidate) > options.TargetTokens)
                {
                    foreach (var p in SplitOversized(Measure(sb.ToString(), piece.Page), s + 1)) yield return p;
                    sb.Clear().Append(part);
                }
                else
                {
                    sb.Clear().Append(candidate);
                }
            }
            if (sb.Length > 0)
            {
                foreach (var p in SplitOversized(Measure(sb.ToString(), piece.Page), s + 1)) yield return p;
            }
            yield break;
        }

        yield return piece;
    }
}
