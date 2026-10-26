namespace ChatBot.Api.Llm;

/// <summary>Bound from the "Llama" section of appsettings.json.</summary>
public sealed class LlamaOptions
{
    public const string SectionName = "Llama";

    /// <summary>Absolute, or relative to the content root.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>KV cache size, and the hard ceiling on prompt + response. Costs RAM
    /// roughly linearly, so it's sized for the machine rather than for Qwen2.5's 32k.</summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Reserved out of ContextSize before any history is packed.</summary>
    public int MaxResponseTokens { get; set; } = 512;

    /// <summary>Slack for the BOS token and tokenizer disagreement. Overflow shows up as
    /// garbage output rather than an exception, so it's worth paying for.</summary>
    public int SafetyMarginTokens { get; set; } = 64;

    /// <summary>Null lets us default to physical cores.</summary>
    public int? Threads { get; set; }

    /// <summary>0 = CPU only.</summary>
    public int GpuLayerCount { get; set; }

    public uint BatchSize { get; set; } = 512;

    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int TopK { get; set; } = 40;
    public float RepeatPenalty { get; set; } = 1.1f;

    /// <summary>Set an integer to make replies reproducible while debugging.</summary>
    public uint? Seed { get; set; }
}
