using System.Text;
using ChatBot.Api.Domain;

namespace ChatBot.Api.Llm;

// ChatML, which is what Qwen2.5-Instruct was fine-tuned on:
//
//   <|im_start|>system\nYou are...<|im_end|>\n
//   <|im_start|>user\nWhat is RAG?<|im_end|>\n
//   <|im_start|>assistant\n              <- primer; the model writes from here
//
// Rendered by hand rather than through LLamaSharp's ApplyTemplate so the prompt is a
// plain string we can log and read. Feed a chat model the wrong delimiters and it gets
// vaguely worse rather than failing, so being able to see the real prompt matters.
public sealed class ChatMlTemplate : IChatTemplate
{
    private const string Start = "<|im_start|>";
    private const string End = "<|im_end|>";

    public IReadOnlyList<string> StopSequences { get; } = [End, Start];

    public string RenderMessage(ChatRole role, string content)
    {
        var sb = new StringBuilder();
        sb.Append(Start).Append(RoleName(role)).Append('\n');
        sb.Append(content.Trim());
        sb.Append(End).Append('\n');
        return sb.ToString();
    }

    // Without the dangling opener the model carries on writing the user's turn.
    public string RenderGenerationPrimer() => $"{Start}assistant\n";

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unmapped chat role.")
    };
}
