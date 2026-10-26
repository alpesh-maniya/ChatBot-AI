using ChatBot.Api.Domain;

namespace ChatBot.Api.Llm;

// The prompt format belongs to the model, not to us: Qwen/Yi/Hermes use ChatML,
// Llama-3 uses different header tokens. Swapping model family = one new implementation.
public interface IChatTemplate
{
    string RenderMessage(ChatRole role, string content);

    string RenderGenerationPrimer();

    /// <summary>Sequences that halt decoding. A small model will otherwise close its own
    /// turn and then hallucinate the user's next one.</summary>
    IReadOnlyList<string> StopSequences { get; }
}
