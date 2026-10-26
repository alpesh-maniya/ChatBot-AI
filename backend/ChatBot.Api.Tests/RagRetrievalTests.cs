using ChatBot.Rag.Index;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Tests;

public sealed class Bm25IndexTests
{
    private static readonly Bm25Index Index = new(
    [
        "The Federal Depository Library Program has 1,133 libraries.",
        "Revenue totaled $874.5 million in FY 2018.",
        "GPO produced 15,901,600 passports for the State Department."
    ]);

    [Fact]
    public void Ranks_the_document_with_the_exact_term_first()
    {
        Assert.Equal(2, Index.Search("passports produced", 3)[0].Row);
    }

    [Fact]
    public void Matches_numbers_with_or_without_thousands_separators()
    {
        Assert.Equal(2, Index.Search("15901600", 3)[0].Row);
        Assert.Equal(0, Index.Search("1,133 libraries", 3)[0].Row);
    }

    [Fact]
    public void Returns_nothing_for_terms_that_appear_nowhere()
    {
        Assert.Empty(Index.Search("capital of France", 3));
    }
}

public sealed class FlatVectorIndexTests
{
    [Fact]
    public void Ranks_rows_by_dot_product()
    {
        // Three unit vectors in 2-D: along x, along y, and diagonal.
        var d = (float)Math.Sqrt(0.5);
        var index = new FlatVectorIndex([1, 0, 0, 1, d, d], dims: 2);

        var hits = index.Search(new float[] { 1, 0 }, 3);

        Assert.Equal([0, 2, 1], hits.Select(h => h.Row));
        Assert.Equal(1f, hits[0].Score, 3);
    }
}

public sealed class RrfFusionTests
{
    [Fact]
    public void A_document_ranked_well_in_both_lists_beats_one_ranked_first_in_one()
    {
        // 7 is 2nd in both lists; 1 is 1st in vectors only, 9 is 1st in BM25 only.
        var fused = RrfFusion.Fuse([[1, 7, 3], [9, 7, 4]], k: 60);

        Assert.Equal(7, fused[0].Row);
        Assert.Equal(2.0 / 62, fused[0].Score, 6);
    }

    [Fact]
    public void Ties_go_to_the_first_list()
    {
        var fused = RrfFusion.Fuse([[1], [9]], k: 60);

        Assert.Equal([1, 9], fused.Select(f => f.Row));
    }
}

public sealed class QueryBuilderTests
{
    private static readonly QueryBuilder Builder = new(new Dictionary<string, string>
    {
        ["ADS"] = "Agency Distribution Services",
        ["FDLP"] = "Federal Depository Library Program"
    });

    [Fact]
    public void A_short_follow_up_is_searched_together_with_the_previous_question()
    {
        var q = Builder.Build("And in FY 2017?", "What were total revenues in FY 2018?");

        Assert.True(q.UsedPreviousTurn);
        Assert.Equal("What were total revenues in FY 2018? And in FY 2017?", q.Vector);
    }

    [Fact]
    public void A_question_that_refers_back_uses_the_previous_turn()
    {
        Assert.True(Builder.Build("Why did inventory go up that year?", "How many passports were produced?").UsedPreviousTurn);
    }

    [Fact]
    public void A_complete_new_question_stands_alone()
    {
        var q = Builder.Build("How many libraries joined the depository program in FY 2018?", "What were total revenues in FY 2018?");

        Assert.False(q.UsedPreviousTurn);
        Assert.Equal("How many libraries joined the depository program in FY 2018?", q.Vector);
    }

    // Each of these has a pronoun or "compared", and each names its own subject first.
    [Theory]
    [InlineData("What material is the iMac stand made from, and what percentage of it is recycled?")]
    [InlineData("What is the total carbon footprint of the iMac, and how much of that comes from product use versus manufacturing?")]
    [InlineData("How much less energy does iMac use compared to the ENERGY STAR requirement?")]
    [InlineData("What is the growth outlook for South Asia in this report?")]
    [InlineData("What new digital system replaced FDsys, and when was it fully deployed?")]
    public void A_question_with_its_own_subject_is_not_joined_to_an_unrelated_one(string message)
    {
        Assert.False(Builder.Build(message, "What is global growth projected to be in 2026?").UsedPreviousTurn);
    }

    [Theory]
    [InlineData("How does that compare with 2017?")]
    [InlineData("What about FY 2017?")]
    [InlineData("Is it higher than last year?")]
    [InlineData("Tell me more about this")]
    public void Real_follow_ups_still_use_the_previous_turn(string message)
    {
        Assert.True(Builder.Build(message, "What were total revenues in FY 2018?").UsedPreviousTurn);
    }

    [Fact]
    public void Keyword_query_gets_the_expansion_of_an_acronym_and_the_acronym_of_an_expansion()
    {
        Assert.Contains("Agency Distribution Services", Builder.Expand("How did ADS do?"));
        Assert.Contains("FDLP", Builder.Expand("Who joined the Federal Depository Library Program?"));
    }

    [Theory]
    [InlineData("hi", true)]
    [InlineData("Thanks!", true)]
    [InlineData("What were revenues?", false)]
    public void Recognises_small_talk(string message, bool expected)
    {
        Assert.Equal(expected, QueryBuilder.IsSmallTalk(message));
    }
}

public sealed class PassageTrimmerTests
{
    [Fact]
    public void Keeps_the_matching_sentence_and_the_one_before_it_in_order()
    {
        // As in a real annual report, the year is in most sentences and so says nothing
        // about which one answers the question.
        var text = string.Join(' ',
            "GPO completed a major reorganization in FY 2018.",
            "The new structure groups business units by customer.",
            "Revenue totaled $874.5 million while expenses were $821.9 million.",
            "Staff training expanded across all plants in FY 2018.",
            "The Office of Inspector General issued twelve reports in FY 2018.",
            "Passport production continued at both secure facilities in FY 2018.");

        var trimmed = PassageTrimmer.Trim(text, "What were GPO's total revenues in FY 2018 revenue", 140);

        Assert.Contains("Revenue totaled $874.5 million", trimmed);
        Assert.Contains("The new structure groups business units by customer.", trimmed);
        Assert.True(trimmed.IndexOf("new structure", StringComparison.Ordinal) < trimmed.IndexOf("Revenue totaled", StringComparison.Ordinal));
        Assert.True(trimmed.Length <= 200);
    }

    [Fact]
    public void Keeps_a_tables_title_and_header_with_the_matching_rows()
    {
        var table = string.Join('\n',
            "Statements of Revenues (Dollars in Thousands)",
            "2018 | 2017",
            "Printing and binding | $738.0 million | $745.7 million",
            "Appropriations | $119.5 million | $114.5 million",
            "Total operating revenues | $874.5 million | $874.3 million",
            "Supplies and materials | $159.6 million | $200.4 million",
            "Net Income | $52.6 million | $58.9 million");

        var trimmed = PassageTrimmer.Trim(table, "total revenues", 150).Split('\n');

        Assert.Equal("Statements of Revenues (Dollars in Thousands)", trimmed[0]);
        Assert.Equal("2018 | 2017", trimmed[1]);
        Assert.Contains("Total operating revenues | $874.5 million | $874.3 million", trimmed);
        Assert.DoesNotContain("Supplies and materials | $159.6 million | $200.4 million", trimmed);
    }

    [Fact]
    public void Leaves_short_passages_alone()
    {
        Assert.Equal("Short passage.", PassageTrimmer.Trim("Short passage.", "anything", 700));
        Assert.Equal("Short passage.", PassageTrimmer.Trim("Short passage.", "anything", 0));
    }
}
