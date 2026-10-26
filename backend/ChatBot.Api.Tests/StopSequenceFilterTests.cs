using ChatBot.Api.Llm;

namespace ChatBot.Api.Tests;

public sealed class StopSequenceFilterTests
{
    private static StopSequenceFilter Build() => new(["<|im_end|>", "<|im_start|>"]);

    [Fact]
    public void Passes_ordinary_text_through()
    {
        var filter = Build();

        var output = filter.Push("Hello there") + filter.Flush();

        Assert.Equal("Hello there", output);
    }

    [Fact]
    public void Cuts_the_text_at_a_stop_sequence()
    {
        var filter = Build();

        var output = filter.Push("The answer is 42.<|im_end|>");

        Assert.Equal("The answer is 42.", output);
        Assert.True(filter.Stopped);
    }

    [Fact]
    public void Catches_a_stop_sequence_split_across_chunks()
    {
        // This is the case that motivates the class: llama.cpp decodes token by token,
        // so "<|im_end|>" routinely arrives in three or four pieces.
        var filter = Build();
        var output = "";

        foreach (var chunk in new[] { "Done.", "<|im", "_end", "|>" })
        {
            output += filter.Push(chunk);
        }

        Assert.Equal("Done.", output);
        Assert.True(filter.Stopped);
    }

    [Fact]
    public void Emits_nothing_further_once_stopped()
    {
        var filter = Build();
        filter.Push("text<|im_end|>");

        Assert.Equal("", filter.Push("leaked continuation"));
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void Flush_releases_held_back_text_when_the_model_stops_on_its_own()
    {
        var filter = Build();

        // "<|im" looks like the start of a stop sequence, so it is withheld...
        var streamed = filter.Push("Almost<|im");
        Assert.DoesNotContain("<|im", streamed);

        // ...until flush proves it was just ordinary text.
        Assert.Equal("Almost<|im", streamed + filter.Flush());
    }

    [Fact]
    public void Withholds_no_more_than_the_longest_stop_sequence_requires()
    {
        var filter = Build();

        // 11 characters for "<|im_start|>" minus one; everything before that is safe.
        var emitted = filter.Push("0123456789ABCDEFGHIJ");

        Assert.Equal("012345678", emitted);
    }
}
