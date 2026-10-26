using ChatBot.Api.Chat;
using ChatBot.Api.Domain;
using ChatBot.Api.Llm;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Tests;

public sealed class GroundedPromptTests
{
    private static ContextWindowPolicy Build(uint contextSize = 2000) => new(
        new ChatMlTemplate(),
        Options.Create(new LlamaOptions { ModelPath = "fake.gguf", ContextSize = contextSize, MaxResponseTokens = 200, SafetyMarginTokens = 0 }),
        Options.Create(new ChatOptions { SystemPrompt = "CHAT PROMPT" }));

    private static GroundingSource Source(int n, int words) =>
        new(n, $"Section {n}, Annual Report p. {n}", string.Join(' ', Enumerable.Repeat($"fact{n}", words)));

    private static GroundingContext Grounding(params GroundingSource[] sources) =>
        new("RAG PROMPT", sources, MaxResponseTokens: 100, AnswerInstruction: "Cite like [1].");

    [Fact]
    public void Uses_the_grounded_system_prompt_and_puts_sources_above_the_question()
    {
        var plan = Build().Compose([ChatMessage.Create(ChatRole.User, "What were revenues?")], new FakeTokenCounter(), Grounding(Source(1, 5), Source(2, 5)));

        Assert.StartsWith("<|im_start|>system\nRAG PROMPT<|im_end|>", plan.Prompt);
        Assert.Contains("Sources:\n[1] (Section 1, Annual Report p. 1)\nfact1", plan.Prompt);
        Assert.True(plan.Prompt.IndexOf("[2] (Section 2", StringComparison.Ordinal) < plan.Prompt.IndexOf("Question: What were revenues?", StringComparison.Ordinal));
        Assert.Contains("Question: What were revenues?\n\nCite like [1].", plan.Prompt);
        Assert.Equal(2, plan.SourceCount);
        Assert.Equal(100, plan.ResponseBudget);
    }

    [Fact]
    public void Drops_the_lowest_ranked_sources_first_when_the_window_is_tight()
    {
        // The fake counter is 4 characters a token, so each source is ~300 tokens and a
        // 600-token window (100 of it reserved) fits one, not four.
        var plan = Build(contextSize: 600).Compose(
            [ChatMessage.Create(ChatRole.User, "What were revenues?")],
            new FakeTokenCounter(),
            Grounding(Source(1, 200), Source(2, 200), Source(3, 200), Source(4, 200)));

        Assert.InRange(plan.SourceCount, 1, 3);
        Assert.Equal(4 - plan.SourceCount, plan.DroppedSources);
        Assert.Contains("[1] (Section 1", plan.Prompt);
        Assert.DoesNotContain("[4] (Section 4", plan.Prompt);
        Assert.Contains("Question: What were revenues?", plan.Prompt);
    }

    [Fact]
    public void Strips_old_citations_from_earlier_answers()
    {
        var history = new[]
        {
            ChatMessage.Create(ChatRole.User, "What were revenues?"),
            ChatMessage.Create(ChatRole.Assistant, "Revenue was $874.5 million [1]."),
            ChatMessage.Create(ChatRole.User, "And in FY 2017?")
        };

        var plan = Build().Compose(history, new FakeTokenCounter(), Grounding(Source(1, 5)) with { FollowUpOf = "What were revenues?" });

        Assert.Contains("Revenue was $874.5 million.", plan.Prompt);
        Assert.Contains("(This follows the earlier question: \"What were revenues?\")", plan.Prompt);
        Assert.Equal(3, plan.IncludedMessages);
    }

    [Fact]
    public void A_new_question_gets_no_earlier_answers_in_its_prompt()
    {
        var history = new[]
        {
            ChatMessage.Create(ChatRole.User, "What is global growth projected to be in 2026?"),
            ChatMessage.Create(ChatRole.Assistant, "Global growth is projected at 2.3 percent [1]."),
            ChatMessage.Create(ChatRole.User, "What is the total carbon footprint of the iMac?")
        };

        var plan = Build().Compose(history, new FakeTokenCounter(), Grounding(Source(1, 5)));

        Assert.DoesNotContain("2.3 percent", plan.Prompt);
        Assert.DoesNotContain("global growth", plan.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Question: What is the total carbon footprint of the iMac?", plan.Prompt);
        Assert.Equal(1, plan.IncludedMessages);
    }

    [Fact]
    public void Without_grounding_the_plain_chat_prompt_is_unchanged()
    {
        var plan = Build().Compose([ChatMessage.Create(ChatRole.User, "hello")], new FakeTokenCounter());

        Assert.StartsWith("<|im_start|>system\nCHAT PROMPT<|im_end|>", plan.Prompt);
        Assert.DoesNotContain("Sources:", plan.Prompt);
        Assert.Equal(0, plan.SourceCount);
    }
}

public sealed class CitationValidatorTests
{
    private static readonly string[] Sources =
    [
        "Revenue totaled $874.5 million while expenses were $821.9 million.",
        "GPO produced 15,901,600 passports and 5,014,573 secure credentials."
    ];

    [Fact]
    public void Removes_citations_to_sources_that_were_not_sent()
    {
        var check = CitationValidator.Check("Revenue was $874.5 million [1][7].", Sources);

        Assert.Equal("Revenue was $874.5 million [1].", check.Text);
        Assert.Equal([1], check.Cited);
        Assert.Equal(1, check.RemovedMarkers);
    }

    [Fact]
    public void Cites_the_source_that_contains_an_uncited_figure()
    {
        var check = CitationValidator.Check("GPO produced 15,901,600 passports in FY 2018.", Sources);

        Assert.Equal("GPO produced 15,901,600 passports in FY 2018 [2].", check.Text);
        Assert.Equal([2], check.Cited);
        Assert.Equal(1, check.AutoCited);
    }

    [Fact]
    public void Does_not_cite_on_years_or_partial_numbers()
    {
        var check = CitationValidator.Check("In 2018 the figure was 74.5 percent.", Sources);

        Assert.Empty(check.Cited);
        Assert.Equal(0, check.AutoCited);
    }

    [Fact]
    public void Keeps_list_layout()
    {
        var check = CitationValidator.Check("Two figures:\n1. Revenue $874.5 million\n2. Passports 15,901,600", Sources);

        Assert.Equal("Two figures:\n1. Revenue $874.5 million [1]\n2. Passports 15,901,600 [2]", check.Text);
    }
}

public sealed class ThousandsConversionTests
{
    [Fact]
    public void Converts_statement_figures_to_millions_and_keeps_negatives()
    {
        var text = ChatService.ExpressThousands("Net Income | $ 52,624 | $ 58,876\nLess: Reserve | (28,745) | (14,783)\nTotal Assets | $ 1,018,063 | 945,623");

        Assert.Contains("Net Income | $52.6 million | $58.9 million", text);
        Assert.Contains("Less: Reserve | -$28.7 million | -$14.8 million", text);
        Assert.Contains("Total Assets | $1.02 billion | $945.6 million", text);
    }
}

public sealed class EmptyReplyTests
{
    [Fact]
    public void Collapses_a_marker_repeated_after_the_full_stop()
    {
        var check = CitationValidator.Check("The office is at 251 Little Falls Drive [1]. [1]", ["251 Little Falls Drive"]);

        Assert.Equal("The office is at 251 Little Falls Drive [1].", check.Text);
    }

    [Fact]
    public void Moves_a_marker_written_after_the_full_stop_instead_of_citing_twice()
    {
        var check = CitationValidator.Check("GPO staff visited 166 depository libraries in FY 2018. [1]", ["staff visited 166 depository libraries"]);

        Assert.Equal("GPO staff visited 166 depository libraries in FY 2018 [1].", check.Text);
        Assert.Equal(0, check.AutoCited);
    }

    [Fact]
    public void Drops_a_source_list_the_model_appends_itself()
    {
        var check = CitationValidator.Check("Global growth could fall to 1.3 percent [1].\n\nSources:\n[1]", ["growth could fall to just 1.3 percent"]);

        Assert.Equal("Global growth could fall to 1.3 percent [1].", check.Text);
    }

    [Fact]
    public void Quotes_the_sentence_that_answers_the_question()
    {
        string[] sources =
        [
            "2.6.5 fees and expenses of the Fund's registered agent in the State of Delaware.",
            "2.1.3 The registered office of the Fund shall be at 251 Little Falls Drive. The registered agent of the Fund at such office shall be Corporation Service Company."
        ];

        var quote = CitationValidator.QuoteBest("Who is the registered agent of the Fund?", sources);

        Assert.NotNull(quote);
        Assert.Equal(2, quote.Value.Source);
        Assert.Equal("The registered agent of the Fund at such office shall be Corporation Service Company.", quote.Value.Sentence);
    }

    [Fact]
    public void Quotes_nothing_when_no_sentence_shares_two_words_with_the_question()
    {
        Assert.Null(CitationValidator.QuoteBest("What is the capital of France?", ["Revenue was $874.5 million."]));
    }
}

public sealed class OnlyMarkersTests
{
    [Theory]
    [InlineData("[1]", true)]
    [InlineData("[2].", true)]
    [InlineData("[1, 3]", true)]
    [InlineData("1,133 [1]", false)]
    [InlineData("$52.6 million [1]", false)]
    public void Detects_a_reply_with_no_answer_in_it(string reply, bool expected)
    {
        Assert.Equal(expected, CitationValidator.IsOnlyMarkers(reply));
    }
}
