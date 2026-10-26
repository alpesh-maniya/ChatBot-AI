using ChatBot.Api.Domain;
using ChatBot.Api.Llm;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Tests;

public sealed class ContextWindowPolicyTests
{
    private static ContextWindowPolicy Build(uint contextSize = 1000, int maxResponse = 200, string system = "You are a helpful assistant.")
    {
        var llama = Options.Create(new LlamaOptions
        {
            ModelPath = "fake.gguf",
            ContextSize = contextSize,
            MaxResponseTokens = maxResponse,
            SafetyMarginTokens = 0
        });

        var chat = Options.Create(new ChatOptions { SystemPrompt = system });

        return new ContextWindowPolicy(new ChatMlTemplate(), llama, chat);
    }

    private static ChatMessage User(string text) => ChatMessage.Create(ChatRole.User, text);
    private static ChatMessage Assistant(string text) => ChatMessage.Create(ChatRole.Assistant, text);

    [Fact]
    public void Includes_the_whole_history_when_it_fits()
    {
        var history = new[] { User("What is RAG?"), Assistant("Retrieval augmented generation."), User("Its components?") };

        var plan = Build().Compose(history, new FakeTokenCounter());

        Assert.Equal(3, plan.IncludedMessages);
        Assert.Equal(0, plan.DroppedMessages);
        Assert.False(plan.ContentTruncated);
    }

    [Fact]
    public void Always_starts_with_the_system_prompt()
    {
        var plan = Build(system: "SYSTEM MARKER").Compose([User("hello")], new FakeTokenCounter());

        Assert.StartsWith("<|im_start|>system\nSYSTEM MARKER<|im_end|>\n", plan.Prompt);
    }

    [Fact]
    public void Ends_with_the_assistant_primer_so_the_model_knows_whose_turn_it_is()
    {
        var plan = Build().Compose([User("hello")], new FakeTokenCounter());

        Assert.EndsWith("<|im_start|>assistant\n", plan.Prompt);
    }

    [Fact]
    public void Keeps_messages_in_chronological_order_even_though_packing_runs_backwards()
    {
        var history = new[] { User("first"), Assistant("second"), User("third") };

        var plan = Build().Compose(history, new FakeTokenCounter());

        var first = plan.Prompt.IndexOf("first", StringComparison.Ordinal);
        var second = plan.Prompt.IndexOf("second", StringComparison.Ordinal);
        var third = plan.Prompt.IndexOf("third", StringComparison.Ordinal);

        Assert.True(first < second && second < third, "History must reach the model in the order it happened.");
    }

    [Fact]
    public void Drops_the_oldest_turns_first_when_the_budget_is_tight()
    {
        // Budget leaves room for roughly two of these, not five.
        var policy = Build(contextSize: 260, maxResponse: 100);
        var history = new[]
        {
            User(new string('a', 120)),
            Assistant(new string('b', 120)),
            User(new string('c', 120)),
            Assistant(new string('d', 120)),
            User(new string('e', 120))
        };

        var plan = policy.Compose(history, new FakeTokenCounter());

        Assert.True(plan.DroppedMessages > 0, "Something should have been evicted.");
        Assert.Contains(new string('e', 120), plan.Prompt);        // newest survives
        Assert.DoesNotContain(new string('a', 120), plan.Prompt);  // oldest goes first
    }

    [Fact]
    public void The_system_prompt_survives_even_when_history_does_not()
    {
        var policy = Build(contextSize: 260, maxResponse: 100, system: "SYSTEM MARKER");
        var history = Enumerable.Range(0, 20).Select(i => User(new string('x', 200))).ToArray();

        var plan = policy.Compose(history, new FakeTokenCounter());

        Assert.Contains("SYSTEM MARKER", plan.Prompt);
    }

    [Fact]
    public void Truncates_rather_than_discards_a_newest_message_that_cannot_fit()
    {
        var policy = Build(contextSize: 400, maxResponse: 100);
        var history = new[] { User("tail marker " + new string('z', 4000)) };

        var plan = policy.Compose(history, new FakeTokenCounter());

        Assert.True(plan.ContentTruncated);
        Assert.Equal(1, plan.IncludedMessages);
        // The tail is kept, because that is where the actual question usually is.
        Assert.Contains("zzz", plan.Prompt);
        Assert.Contains("omitted to fit the context window", plan.Prompt);
    }

    [Fact]
    public void Fails_loudly_when_the_configuration_leaves_no_room_for_history()
    {
        var policy = Build(contextSize: 100, maxResponse: 95, system: new string('s', 400));

        var ex = Assert.Throws<InvalidOperationException>(
            () => policy.Compose([User("hi")], new FakeTokenCounter()));

        Assert.Contains("Context budget exhausted", ex.Message);
    }

    [Fact]
    public void Reports_a_prompt_token_count_that_matches_the_prompt_it_produced()
    {
        var counter = new FakeTokenCounter();

        var plan = Build().Compose([User("hello"), Assistant("hi")], counter);

        Assert.Equal(counter.Count(plan.Prompt), plan.PromptTokens);
    }
}
