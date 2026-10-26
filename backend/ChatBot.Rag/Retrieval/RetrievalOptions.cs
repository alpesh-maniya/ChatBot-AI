namespace ChatBot.Rag.Retrieval;

public sealed record RetrievalOptions
{
    public int VectorTopK { get; set; } = 20;
    public int Bm25TopK { get; set; } = 20;

    /// <summary>The 60 in 1/(60 + rank). Larger flattens the difference between ranks.</summary>
    public int RrfK { get; set; } = 60;

    // Measured: five sources at ~1,800 tokens made the CPU prefill alone take a minute.
    public int MaxSources { get; set; } = 4;

    /// <summary>Embedding-model tokens across all sources sent to the LLM.</summary>
    public int SourceTokenBudget { get; set; } = 1400;

    /// <summary>Best cosine score below this = "not in the report". Tuned from the eval run.</summary>
    public float MinVectorScore { get; set; } = 0.55f;

    /// <summary>Longest passage, in characters, the model is shown; longer ones are cut to
    /// the sentences or rows matching the question. 0 = send whole chunks.</summary>
    public int PassageMaxChars { get; set; }

    /// <summary>The top-ranked passages sent whole regardless of PassageMaxChars. Measured:
    /// trimming all four cut the answer out of passage [1] on 7 of 26 questions; [1] holds
    /// the answer far more often than the rest, so it is the one not to cut.</summary>
    public int WholePassages { get; set; } = 1;

    /// <summary>With several documents, one whose best cosine is more than this below the
    /// best document's is left out of the search. 0 = search them all.</summary>
    public float DocumentMargin { get; set; }
}
