namespace ChatBot.Api.Llm;

// Separate from ILanguageModel so ContextWindowPolicy can be tested against a fake
// counter instead of loading a gigabyte of weights.
public interface ITokenCounter
{
    /// <summary>Token count using the model's own vocabulary, counting specials such as
    /// &lt;|im_start|&gt; as the single tokens they are.</summary>
    int Count(string text);
}
