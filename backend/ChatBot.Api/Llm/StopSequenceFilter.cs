using System.Text;

namespace ChatBot.Api.Llm;

// Keeps a stop sequence from showing up in the browser.
//
// "<|im_end|>" comes back from the decoder in pieces - "<|im", "_end", "|>". Inference
// halts once the whole thing is recognised, but the leading pieces have already been
// streamed and rendered by then. So we hold back any trailing text that could still
// turn into a stop sequence: at most (longest stop sequence - 1) characters, which is
// well under a token of latency.
public sealed class StopSequenceFilter(IReadOnlyList<string> stopSequences)
{
    private readonly StringBuilder _buffer = new();
    private readonly int _holdback = stopSequences.Count == 0 ? 0 : stopSequences.Max(s => s.Length) - 1;

    public bool Stopped { get; private set; }

    /// <summary>Takes one decoded chunk, returns whatever is now safe to emit.</summary>
    public string Push(string chunk)
    {
        if (Stopped) return string.Empty;

        _buffer.Append(chunk);
        var text = _buffer.ToString();

        var cut = -1;
        foreach (var stop in stopSequences)
        {
            var index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (cut < 0 || index < cut)) cut = index;
        }

        if (cut >= 0)
        {
            Stopped = true;
            _buffer.Clear();
            return text[..cut];
        }

        if (text.Length <= _holdback) return string.Empty;

        var safeLength = text.Length - _holdback;
        var safe = text[..safeLength];
        _buffer.Remove(0, safeLength);
        return safe;
    }

    /// <summary>Releases the held-back tail once the model stops on its own.</summary>
    public string Flush()
    {
        if (Stopped) return string.Empty;
        var rest = _buffer.ToString();
        _buffer.Clear();
        return rest;
    }
}
