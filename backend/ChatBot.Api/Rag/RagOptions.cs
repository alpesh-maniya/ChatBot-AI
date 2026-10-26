using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Rag;

/// <summary>Bound from the "Rag" section of appsettings.json.</summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Off = the plain chatbot from before, unchanged.</summary>
    public bool Enabled { get; set; }

    /// <summary>PDFs that ship with the app, each with an optional &lt;name&gt;.profile.json.
    /// Absolute, or relative to the content root, like the other folders.</summary>
    public string SourceFolder { get; set; } = "";

    /// <summary>Where uploaded PDFs are kept. Uploads can be deleted; source PDFs can't.</summary>
    public string UploadFolder { get; set; } = "";

    /// <summary>One sub-folder per document id, written by the ingest pipeline.</summary>
    public string IndexFolder { get; set; } = "";

    public int MaxUploadMegabytes { get; set; } = 50;

    public EmbeddingOptions Embedding { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public SamplingOptions Sampling { get; set; } = new();

    // The three texts below are templates, filled per question from the documents in the
    // conversation's scope:
    //   {subject}    "the U.S. Government Publishing Office (GPO) 2018 Annual Report", or
    //                "these documents: the A, the B" for several
    //   {documents}  "the GPO 2018 Annual Report", or "the selected documents"
    //   {fallback}   FallbackMessage, already filled in

    /// <summary>Sent as the reply, without calling the LLM, when retrieval finds nothing relevant.</summary>
    public string FallbackMessage { get; set; } = "I couldn't find that in {documents}.";

    /// <summary>Replaces Chat:SystemPrompt for grounded answers.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Appended after the question in the user turn - the rules that matter most,
    /// restated where a small model actually pays attention.</summary>
    public string AnswerInstruction { get; set; } = "";
}

// Grounded answers want less creativity than chat: the model should copy figures out of
// the sources, not paraphrase them.
public sealed class SamplingOptions
{
    public float? Temperature { get; set; } = 0.2f;
    public float? TopP { get; set; }
    public int? TopK { get; set; }

    /// <summary>A high penalty pushes the model away from repeating the exact numbers it was given.</summary>
    public float? RepeatPenalty { get; set; } = 1.05f;

    public int? MaxResponseTokens { get; set; } = 384;
}
