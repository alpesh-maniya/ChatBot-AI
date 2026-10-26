using ChatBot.Api.Llm;

namespace ChatBot.Api.Tests;

// One token per four characters. Inaccurate on purpose - these tests are about the
// packing decisions, not the tokenizer, and a fake keeps the suite off the weights.
public sealed class FakeTokenCounter : ITokenCounter
{
    public int Count(string text) => (int)Math.Ceiling(text.Length / 4.0);
}
