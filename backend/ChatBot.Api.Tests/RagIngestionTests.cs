using ChatBot.Rag.Chunking;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Ingestion;

namespace ChatBot.Api.Tests;

// One token per word. As with FakeTokenCounter, these tests are about where the
// boundaries fall, not about the real tokenizer.
internal sealed class WordTokenizer : ITextTokenizer
{
    public int CountTokens(string text) => text.Split(' ', '\n').Count(w => w.Length > 0);
}

public sealed class TextCleanerTests
{
    private static readonly TextCleaner Cleaner = new(["publishing", "document"]);

    [Fact]
    public void Joins_a_word_broken_across_lines_when_the_whole_word_appears_elsewhere()
    {
        Assert.Equal("Government publishing office", Cleaner.JoinLines(["Government publish-", "ing office"]));
    }

    [Fact]
    public void Keeps_the_hyphen_of_a_real_compound()
    {
        Assert.Equal("capital passport-related assets", Cleaner.JoinLines(["capital passport-", "related assets"]));
    }

    [Fact]
    public void Keeps_the_hyphen_before_a_capital_or_after_a_digit()
    {
        Assert.Equal("Director Davita Vance-Cooks", Cleaner.JoinLines(["Director Davita Vance-", "Cooks"]));
        Assert.Equal("the 1,242-page directory", Cleaner.JoinLines(["the 1,242-", "page directory"]));
    }

    [Fact]
    public void Leaves_figures_exactly_as_printed()
    {
        var text = Cleaner.JoinLines(["Net income $ 52,624 and (28,745)", "reserve of $874,466,000."]);

        Assert.Contains("$ 52,624", text);
        Assert.Contains("(28,745)", text);
        Assert.Contains("$874,466,000.", text);
    }

    [Fact]
    public void Normalises_ligatures_quotes_and_tabs()
    {
        Assert.Equal("GPO's \"official\" final", TextCleaner.Normalize("GPO’s “official” ﬁnal"));
        Assert.Equal("December 14, 2018", Cleaner.JoinLines(["December\t14,\t2018"]));
    }
}

public sealed class TableStitcherTests
{
    private static LayoutLine Line(string text, double left, double y) =>
        new(new Box(left, y, left + text.Length * 5, y + 8), [new LayoutWord(text, new Box(left, y, left + text.Length * 5, y + 8), "Helvetica", 9)]);

    [Fact]
    public void Rebuilds_rows_from_column_blocks()
    {
        // Three columns, as PdfPig returns them: one block per column.
        LayoutLine[] lines =
        [
            Line("Fiscal Year", 80, 500), Line("1980", 80, 486), Line("1985", 80, 472),
            Line("Appropriation", 150, 500), Line("$ 91.6 million", 150, 486), Line("94.0 million", 150, 472),
            Line("In Constant Dollars", 226, 500), Line("$ 285.4 million", 226, 486), Line("220.5 million", 226, 472)
        ];

        var rows = TableStitcher.Stitch(lines).Select(TableStitcher.Render).ToList();

        Assert.Equal(
            ["Fiscal Year | Appropriation | In Constant Dollars", "1980 | $ 91.6 million | $ 285.4 million", "1985 | 94.0 million | 220.5 million"],
            rows);
    }

    [Fact]
    public void Joins_a_lone_currency_sign_to_its_value()
    {
        LayoutLine[] lines = [Line("Unrestricted", 110, 560), Line("$", 342, 560), Line("321,000", 381, 560)];

        Assert.Equal("Unrestricted | $321,000", TableStitcher.Render(TableStitcher.Stitch(lines)[0]));
    }
}

public sealed class SentenceSplitterTests
{
    [Fact]
    public void Does_not_split_on_abbreviations_initials_or_decimals()
    {
        var sentences = SentenceSplitter.Split(
            "The U.S. Government Publishing Office reported $874.5 million. It was signed by John W. Crawford. Next.");

        Assert.Equal(
            ["The U.S. Government Publishing Office reported $874.5 million.", "It was signed by John W. Crawford.", "Next."],
            sentences);
    }

    [Fact]
    public void Keeps_a_closing_quote_with_its_sentence()
    {
        var sentences = SentenceSplitter.Split("He said \"Keeping America Informed.\" Then he left.");

        Assert.Equal("He said \"Keeping America Informed.\"", sentences[0]);
    }
}

public sealed class SectionChunkerTests
{
    private static readonly IngestProfile Profile = new()
    {
        DocId = "doc",
        Title = "Test Report",
        Parts = [new PartRange("Main", "Report", 1, 100)]
    };

    private static SourceBlock Para(string text, int page = 1, params string[] path) =>
        new() { Kind = BlockKind.Paragraph, Text = text, PdfPage = page, Part = "Main", SectionPath = path };

    private static SourceBlock Heading(string text, int level, params string[] path) =>
        new() { Kind = BlockKind.Heading, Text = text, PdfPage = 1, Part = "Main", HeadingLevel = level, SectionPath = path };

    private static string Sentence(int i) => $"Sentence number {i} has exactly eight words here.";

    private static IReadOnlyList<DocumentChunk> Chunk(ChunkingOptions options, params SourceBlock[] blocks) =>
        new SectionChunker(options, new WordTokenizer())
            .Chunk(new AssembledDocument(blocks, new Dictionary<string, string>(), new Dictionary<int, string> { [1] = "7" }, []), Profile);

    [Fact]
    public void No_chunk_is_larger_than_the_target_plus_header()
    {
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(Sentence));
        var options = new ChunkingOptions { TargetTokens = 40, MaxTokens = 60, OverlapTokens = 10, MinTokens = 5 };

        var chunks = Chunk(options, Para(text, 1, "A"));

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(new WordTokenizer().CountTokens(c.Text) <= 40, $"{c.Id} too long"));
    }

    [Fact]
    public void Consecutive_chunks_overlap_by_whole_sentences()
    {
        var text = string.Join(' ', Enumerable.Range(0, 20).Select(Sentence));
        var options = new ChunkingOptions { TargetTokens = 40, MaxTokens = 60, OverlapTokens = 10, MinTokens = 5 };

        var chunks = Chunk(options, Para(text, 1, "A"));

        var lastOfFirst = SentenceSplitter.Split(chunks[0].Text)[^1];
        Assert.StartsWith(lastOfFirst, chunks[1].Text);
    }

    [Fact]
    public void A_section_heading_closes_the_chunk_and_sets_the_path()
    {
        var options = new ChunkingOptions { TargetTokens = 200, MinTokens = 5 };

        var chunks = Chunk(options,
            Heading("Revenue", 2, "Revenue"),
            Para(Sentence(1), 1, "Revenue"),
            Heading("Passports", 2, "Passports"),
            Para(Sentence(2), 1, "Passports"));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["Revenue"], chunks[0].SectionPath);
        Assert.Equal(["Passports"], chunks[1].SectionPath);
        Assert.Equal("Test Report | Passports | Report p. 7", chunks[1].Header);
    }

    [Fact]
    public void A_long_table_repeats_its_title_and_header_row_in_every_piece()
    {
        var rows = new[] { "Item | 2018 | 2017" }.Concat(Enumerable.Range(0, 30).Select(i => $"Line item {i} | {i},000 | {i},500")).ToArray();
        var table = new SourceBlock
        {
            Kind = BlockKind.Table, Text = string.Join('\n', rows), Rows = rows, Title = "Summary (in thousands)",
            Unit = "in thousands", PdfPage = 1, Part = "Main", SectionPath = ["Finances"]
        };

        var chunks = Chunk(new ChunkingOptions { TableMaxTokens = 40 }, table);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c =>
        {
            Assert.Equal(ChunkKind.Table, c.Kind);
            Assert.StartsWith("Summary (in thousands)", c.Text);
            Assert.Contains("Item | 2018 | 2017", c.Text);
            Assert.EndsWith("| amounts in thousands", c.Header);
        });
    }

    [Fact]
    public void Ids_are_stable_for_the_same_input()
    {
        var text = string.Join(' ', Enumerable.Range(0, 30).Select(Sentence));
        var options = new ChunkingOptions { TargetTokens = 40, OverlapTokens = 10, MinTokens = 5 };

        var a = Chunk(options, Para(text));
        var b = Chunk(options, Para(text));

        Assert.Equal(a.Select(c => (c.Id, c.ContentHash)), b.Select(c => (c.Id, c.ContentHash)));
    }
}

public sealed class PageClassifierTests
{
    private static readonly IngestProfile Profile = new()
    {
        DocId = "doc",
        Title = "Test",
        Parts = [new PartRange("Main", "Report", 1, 10)]
    };

    private static LayoutBlock Block(int order, double left, double top, string font, double size, params string[] lines)
    {
        var layoutLines = lines.Select((text, i) =>
        {
            var y = top - 12 * (i + 1);
            var words = text.Split(' ').Select((w, j) => new LayoutWord(w, new Box(left + j * 30, y, left + j * 30 + 25, y + 8), font, size)).ToArray();
            return new LayoutLine(new Box(left, y, left + text.Length * 5, y + 8), words);
        }).ToArray();

        return new LayoutBlock(order, Box.Union(layoutLines.Select(l => l.Box)), layoutLines);
    }

    [Fact]
    public void Drops_the_footer_and_keeps_the_printed_page_number()
    {
        var page = new PageLayout(14, 612, 792,
        [
            Block(0, 36, 700, "Garamond", 9.7, "GPO publishes the documents required by the legislative process of Congress."),
            Block(1, 328, 41, "Helvetica-Lt", 6, "2018 ANNUAL REPORT | U.S. GOVERNMENT PUBLISHING OFFICE"),
            Block(2, 32, 41, "Helvetica-Lt", 8, "12")
        ]);

        var result = new PageClassifier(Profile, new TextCleaner([])).Classify(page, Profile.Parts[0], 9.7);

        Assert.Equal("12", result.PrintedPage);
        var block = Assert.Single(result.Blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void Joins_a_label_column_and_a_number_column_into_one_table()
    {
        var page = new PageLayout(14, 612, 792,
        [
            Block(0, 37, 578, "Helvetica-Roman", 12, "GPO staff", "FY 2018 revenues", "FY 2018 appropriations"),
            Block(1, 491, 578, "Helvetica-Roman", 12, "1,737", "$874,466,000", "$117,068,000")
        ]);

        var result = new PageClassifier(Profile, new TextCleaner([])).Classify(page, Profile.Parts[0], 9.7);

        var table = Assert.Single(result.Blocks);
        Assert.Equal(BlockKind.Table, table.Kind);
        Assert.Equal(["GPO staff | 1,737", "FY 2018 revenues | $874,466,000", "FY 2018 appropriations | $117,068,000"], table.Rows);
    }

    [Fact]
    public void Splits_a_run_in_heading_from_its_paragraph()
    {
        var bold = new LayoutWord("Results", new Box(36, 600, 70, 608), "HelveticaNeue-Md", 9.7);
        var lead = new LayoutWord("Financial", new Box(0, 600, 30, 608), "HelveticaNeue-Md", 9.7);
        var body = "Revenue totaled $874.5 million while expenses were $821.9 million for the year"
            .Split(' ').Select((w, i) => new LayoutWord(w, new Box(80 + i * 20, 600, 95 + i * 20, 608), "Garamond", 9.7));
        var line = new LayoutLine(new Box(0, 600, 400, 608), [lead, bold, .. body]);
        var page = new PageLayout(25, 612, 792, [new LayoutBlock(0, line.Box, [line])]);

        var result = new PageClassifier(Profile, new TextCleaner([])).Classify(page, Profile.Parts[0], 9.7);

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(BlockKind.Heading, result.Blocks[0].Kind);
        Assert.True(result.Blocks[0].IsRunIn);
        Assert.Equal("Financial Results", result.Blocks[0].Text);
        Assert.StartsWith("Revenue totaled $874.5 million", result.Blocks[1].Text);
    }
}

public sealed class AcronymExtractorTests
{
    [Theory]
    [InlineData("services through its Agency Distribution Services", "ADS", "Agency Distribution Services")]
    [InlineData("Office's Security and Intelligent Documents", "SID", "Security and Intelligent Documents")]
    [InlineData("GPO replaced the original site with its Federal Digital System", "FDsys", "Federal Digital System")]
    public void Expands_inline_definitions(string before, string acronym, string expected)
    {
        Assert.Equal(expected, AcronymExtractor.Expand(before + " ", acronym));
    }

    [Fact]
    public void Gives_up_when_the_words_do_not_match_the_acronym()
    {
        Assert.Null(AcronymExtractor.Expand("this is just some text ", "XYZ"));
    }
}
