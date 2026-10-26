using ChatBot.Rag.Chunking;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Index;
using ChatBot.Rag.Ingestion;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Tests;

// Use case 2: a long report whose regions and chapters all use the same words.

public sealed class SectionRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chatbot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly SectionRoute[] Routes =
    [
        new(["South Asia", "SAR"], "South Asia"),
        new(["East Asia and Pacific", "EAP"], "East Asia and Pacific")
    ];

    internal static RagIndex Build(string root, IReadOnlyList<SectionRoute> routes, bool sectionLead, int tokens,
        params (string[] Path, string Text, float[] Vector)[] rows)
    {
        var chunks = rows.Select((r, i) => new DocumentChunk
        {
            Id = $"gep:{i:D4}", DocId = "gep", Ordinal = i, Kind = ChunkKind.Text, Part = "Document", PartLabel = "",
            SectionPath = r.Path, PdfPageStart = i + 1, PdfPageEnd = i + 1, Header = string.Join(" > ", r.Path), Text = r.Text,
            TokenCount = tokens, ContentHash = i.ToString()
        }).ToArray();

        var manifest = new DocumentManifest
        {
            DocId = "gep", Title = "GEP", SourceFile = "gep.pdf", SourceSha256 = "0", PdfPageCount = rows.Length,
            CreatedAt = DateTimeOffset.UtcNow, EmbeddingModel = "fake.gguf", EmbeddingDimensions = 2,
            ChunkerVersion = DocumentManifest.CurrentChunkerVersion, Chunking = new ChunkingOptions(), ChunkCount = rows.Length,
            Routes = routes, SectionLead = sectionLead
        };

        var folder = Path.Combine(root, "gep-" + Guid.NewGuid().ToString("N")[..6]);
        IndexStore.Write(folder, manifest, chunks, rows.Select(r => r.Vector).ToArray(), new Dictionary<string, string>());
        return RagIndex.Load(folder);
    }

    private RagIndex Regions(IReadOnlyList<SectionRoute> routes) => Build(_root, routes, sectionLead: false, tokens: 10,
        (["Chapter 2: Regional Outlooks", "East Asia and Pacific", "Outlook"], "Growth in EAP is projected to moderate to 4.2 percent", [1f, 0f]),
        (["Chapter 2: Regional Outlooks", "South Asia", "Outlook"], "Growth in SAR is expected to soften to 6.3 percent", [0.9f, 0.44f]),
        (["Chapter 3: Sovereign Debt", "Introduction"], "Rising government debt raises interest rates", [0f, 1f]),
        (["Chapter 1: Global Outlook", "BOX 1.10 Something else"], "Box ten text", [0.5f, 0.5f]),
        (["Chapter 1: Global Outlook", "BOX 1.1 How much will AI affect global growth?"], "About 60 percent of jobs are exposed", [0.5f, 0.5f]));

    [Fact]
    public void A_named_region_limits_search_to_that_region()
    {
        var filter = Regions(Routes).Router.Route("What is the growth outlook for South Asia?");

        Assert.NotNull(filter);
        Assert.Equal(["South Asia"], filter.Sections);
        Assert.Equal([1], filter.Rows);
    }

    [Fact]
    public void Abbreviations_match_only_as_written()
    {
        var router = Regions(Routes).Router;

        Assert.NotNull(router.Route("How is SAR doing?"));
        Assert.Null(router.Route("What did the sar report say?"));
    }

    [Fact]
    public void Topics_are_not_routes()
    {
        Assert.Null(Regions(Routes).Router.Route("What is the growth outlook for developing economies?"));
    }

    [Fact]
    public void Chapter_and_box_numbers_match_their_headings_exactly()
    {
        var router = Regions(Routes).Router;

        Assert.Equal([2], router.Route("What does Chapter 3 say about debt?")!.Rows);
        Assert.Equal([4], router.Route("According to Box 1.1, how many jobs are exposed?")!.Rows);
    }

    [Fact]
    public void Without_routes_in_the_profile_nothing_is_filtered_not_even_chapters()
    {
        Assert.Null(Regions([]).Router.Route("What does Chapter 3 say about debt?"));
    }

    [Fact]
    public async Task The_filtered_search_never_returns_another_region()
    {
        // East Asia scores higher on cosine; the question names South Asia.
        var index = Regions(Routes);
        var retriever = new HybridRetriever(index, new FixedEmbedder([1f, 0f]), new RetrievalOptions { MaxSources = 4 });

        var result = await retriever.RetrieveAsync("What is the growth outlook for South Asia?", null, CancellationToken.None);

        Assert.Equal(["gep:0001"], result.Sources.Select(s => s.Chunk.Id));
        Assert.Equal(["South Asia"], result.Sections!);
    }
}

public sealed class SectionLeadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chatbot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly string[] Outlook = ["Chapter 2: Regional Outlooks", "South Asia", "Outlook"];
    private static readonly string[] Recent = ["Chapter 2: Regional Outlooks", "South Asia", "Recent developments"];

    private RagIndex Build(bool sectionLead) => SectionRoutingTests.Build(_root, [], sectionLead, tokens: 10,
        (Recent, "Inflation remained contained", [0f, 1f]),
        (Outlook, "Growth in SAR is expected to soften to 6.3 percent in 2026", [0.6f, 0.8f]),
        (Outlook, "Growth is then anticipated to rebound", [1f, 0f]),
        (Outlook, "Inflation in the region is projected to rise", [0.99f, 0.14f]));

    [Fact]
    public async Task Two_hits_in_one_section_put_the_section_start_first()
    {
        var retriever = new HybridRetriever(Build(sectionLead: true), new FixedEmbedder([1f, 0f]), new RetrievalOptions { MaxSources = 3 });

        var result = await retriever.RetrieveAsync("growth outlook", null, CancellationToken.None);

        Assert.Equal("gep:0001", result.Sources[0].Chunk.Id);
        Assert.Equal([1, 2, 3], result.Sources.Select(s => s.Number));
        Assert.Equal(3, result.Sources.Count);
    }

    [Fact]
    public async Task Off_by_default()
    {
        var retriever = new HybridRetriever(Build(sectionLead: false), new FixedEmbedder([1f, 0f]), new RetrievalOptions { MaxSources = 3 });

        var result = await retriever.RetrieveAsync("growth outlook", null, CancellationToken.None);

        Assert.Equal("gep:0002", result.Sources[0].Chunk.Id);
    }

    [Fact]
    public void Section_start_skips_back_over_tables_and_stops_at_the_previous_heading()
    {
        var index = Build(sectionLead: true);

        Assert.Equal("gep:0001", index.SectionStart(index.Chunks[3]).Id);
        Assert.Equal("gep:0000", index.SectionStart(index.Chunks[0]).Id);
    }
}

public sealed class PromptBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chatbot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(0, 3)]      // whole chunks: 400 + 400 + 400 fits 1,400, a fourth doesn't
    [InlineData(700, 4)]    // trimmed after [1]: 400 + 3 x 175
    public async Task Passages_are_charged_what_the_prompt_will_spend(int passageMaxChars, int expected)
    {
        var index = SectionRoutingTests.Build(_root, [], false, tokens: 400,
            (["A"], "one", [1f, 0f]), (["B"], "two", [0.9f, 0.44f]), (["C"], "three", [0.8f, 0.6f]), (["D"], "four", [0.7f, 0.71f]));
        var options = new RetrievalOptions { MaxSources = 4, SourceTokenBudget = 1400, PassageMaxChars = passageMaxChars, WholePassages = 1 };

        var result = await new HybridRetriever(index, new FixedEmbedder([1f, 0f]), options).RetrieveAsync("x", null, CancellationToken.None);

        Assert.Equal(expected, result.Sources.Count);
    }
}

public sealed class ProfileHeadingTests
{
    private static LayoutBlock Block(int order, double left, double top, string font, double size, string text)
    {
        var words = text.Split(' ').Select((w, j) => new LayoutWord(w, new Box(left + j * 30, top - size, left + j * 30 + 25, top), font, size)).ToArray();
        var line = new LayoutLine(new Box(left, top - size, left + text.Length * 5, top), words);
        return new LayoutBlock(order, line.Box, [line]);
    }

    private static readonly IngestProfile Profile = new()
    {
        DocId = "gep",
        Title = "GEP",
        Parts = [new PartRange("Chapter2", "", 1, 10, Section: "Chapter 2: Regional Outlooks")],
        HeadingStyles =
        [
            new HeadingStyle("HelveticaLTStd-Bold", 13.5, 16.5, 2),
            new HeadingStyle("HelveticaLTStd-Roman", 10.5, 11.5, 3)
        ],
        DropStyles = [new DropStyle("Helvetica", 6.5)]
    };

    [Fact]
    public void Body_size_headings_in_the_sans_face_get_their_level_and_wrapped_titles_are_joined()
    {
        var page = new PageLayout(2, 612, 792,
        [
            Block(0, 72, 700, "ABCDEF+HelveticaLTStd-Bold", 16, "Key trends in EMDE"),
            Block(1, 72, 680, "ABCDEF+HelveticaLTStd-Bold", 16, "government debt and interest rates"),
            Block(2, 72, 650, "ABCDEF+HelveticaLTStd-Roman", 11, "Outlook"),
            Block(3, 72, 630, "ABCDEF+AGaramondPro-Regular", 11, "Growth in SAR is expected to soften to 6.3 percent in 2026, mainly reflecting the conflict."),
            Block(4, 300, 500, "GHIJKL+Helvetica", 5.7, "2015 2020-24 0.4")
        ]);

        var document = new DocumentAssembler(Profile).Assemble([page]);

        Assert.Equal(
        [
            (BlockKind.Heading, "Key trends in EMDE government debt and interest rates", 2),
            (BlockKind.Heading, "Outlook", 3),
            (BlockKind.Paragraph, "Growth in SAR is expected to soften to 6.3 percent in 2026, mainly reflecting the conflict.", 0)
        ], document.Blocks.Select(b => (b.Kind, b.Text, b.HeadingLevel)));

        Assert.Equal(
            ["Chapter 2: Regional Outlooks", "Key trends in EMDE government debt and interest rates", "Outlook"],
            document.Blocks[^1].SectionPath);
    }
}

// Use case 3: a designed brochure, facts in tables, call-outs and endnotes.
public sealed class DesignedLayoutTests
{
    private static LayoutWord Word(string text, double left, double top, double size, string font = "SFHello-Light") =>
        new(text, new Box(left, top - size, left + text.Length * size * 0.5, top), font, size);

    private static LayoutBlock Block(int order, params LayoutWord[][] lines)
    {
        var layoutLines = lines.Select(words => new LayoutLine(Box.Union(words.Select(w => w.Box)), words)).ToArray();
        return new LayoutBlock(order, Box.Union(layoutLines.Select(l => l.Box)), layoutLines);
    }

    private static LayoutWord[] Line(string text, double left, double top, double size, string font = "SFHello-Light")
    {
        var x = left;
        return text.Split(' ').Select(w =>
        {
            var word = Word(w, x, top, size, font);
            x += w.Length * size * 0.5 + size * 0.3;
            return word;
        }).ToArray();
    }

    private static IngestProfile Profile(bool joinCallouts = false, params ElementRegion[] tables) => new()
    {
        DocId = "imac",
        Title = "iMac",
        Parts = [new PartRange("Document", "", 1, 2)],
        EndnotePages = [2],
        Tables = tables,
        JoinCallouts = joinCallouts
    };

    // "18 Energy consumption ..." on the endnote page: the number in 4.7pt beside the note.
    private static readonly PageLayout Endnotes = new(2, 612, 792,
    [
        Block(0, Line("18", 211, 700, 4.7)),
        Block(1, Line("Energy values are based on the ENERGY STAR Program Requirements.", 217, 700, 7))
    ]);

    [Fact]
    public void A_superscript_marker_leaves_the_sentence_and_its_note_follows_it()
    {
        var claim = Block(0,
            [.. Line("iMac uses 58 percent less energy than the requirement for ENERGY", 216, 676, 11.9),
             Word("18", 600, 676, 8), .. Line("STAR. It is efficient.", 610, 676, 11.9)]);
        var document = new DocumentAssembler(Profile()).Assemble([new PageLayout(1, 612, 792, [claim]), Endnotes]);

        var paragraph = document.Blocks.First(b => b.Kind == BlockKind.Paragraph && b.PdfPage == 1);
        Assert.Equal(
            "iMac uses 58 percent less energy than the requirement for ENERGY STAR. " +
            "(Note 18: Energy values are based on the ENERGY STAR Program Requirements.) It is efficient.",
            paragraph.Text);
        Assert.Contains(document.Blocks, b => b.Text == "Note 18: Energy values are based on the ENERGY STAR Program Requirements.");
    }

    [Fact]
    public void A_table_region_keeps_every_value_next_to_its_label()
    {
        var region = new ElementRegion(1, 210, 500, 600, 660, "Greenhouse gas emissions");
        var page = new PageLayout(1, 612, 792,
        [
            Block(0, Line("Total product footprint", 219, 628, 6.5), Line("Product use", 219, 544, 6.5)),
            Block(1, Line("346 kg CO2e", 436, 629, 6.3), Line("45%", 436, 543, 6.5)),
            Block(2, Line("Percentages may not total 100 due to rounding.", 216, 490, 7))
        ]);

        var document = new DocumentAssembler(Profile(tables: region)).Assemble([page, Endnotes]);

        var table = document.Blocks.Single(b => b.Kind == BlockKind.Table);
        Assert.Equal("Greenhouse gas emissions", table.Title);
        Assert.Equal(["Total product footprint | 346 kg CO2e", "Product use | 45%"], table.Rows);
    }

    [Fact]
    public void A_big_figure_and_its_caption_are_one_paragraph()
    {
        var page = new PageLayout(1, 612, 792,
        [
            Block(0, Line("76%", 36, 302, 17.5, "SFHello-Semibold")),
            Block(1, Line("recycled content in fiber packaging", 36, 280, 9.5))
        ]);

        var document = new DocumentAssembler(Profile(joinCallouts: true)).Assemble([page, Endnotes]);

        Assert.Equal("76% recycled content in fiber packaging", document.Blocks.First(b => b.PdfPage == 1).Text);
    }
}

public sealed class OversizedSentenceTests
{
    private sealed class WordTokenizer : ITextTokenizer
    {
        public int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [Fact]
    public void A_part_still_too_long_after_the_semicolon_split_is_split_again()
    {
        // One "; " and a 300-word list after it, the shape of the World Bank appendix notes.
        var tail = string.Join(", ", Enumerable.Range(1, 150).Select(i => $"country {i}"));
        var block = new SourceBlock
        {
            Kind = BlockKind.Paragraph, Text = "f = forecast; " + tail, PdfPage = 190, Part = "Document", SectionPath = ["Appendix"]
        };
        var profile = IngestProfile.CreateDefault("gep", "GEP", 200);
        var options = new ChunkingOptions { TargetTokens = 60, MaxTokens = 80, OverlapTokens = 10, TableMaxTokens = 80 };

        var chunks = new SectionChunker(options, new WordTokenizer())
            .Chunk(new AssembledDocument([block], new Dictionary<string, string>(), new Dictionary<int, string>(), []), profile);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(new WordTokenizer().CountTokens(c.Text) <= options.MaxTokens, $"{c.Id}: {c.Text.Length} chars"));
    }
}
