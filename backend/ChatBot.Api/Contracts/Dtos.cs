using System.ComponentModel.DataAnnotations;
using ChatBot.Api.Domain;
using ChatBot.Api.Rag;

namespace ChatBot.Api.Contracts;

// Wire shapes, kept separate from the domain types so renaming a domain property can't
// silently break the Angular client.

public sealed record MessageDto(
    Guid Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SourceRefDto> Sources,
    IReadOnlyList<int> CitedSources)
{
    public static MessageDto From(ChatMessage message) =>
        new(message.Id, message.Role.ToString().ToLowerInvariant(), message.Content, message.CreatedAt,
            message.Sources.Select(SourceRefDto.From).ToArray(), message.CitedSources);
}

public sealed record SessionSummaryDto(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int MessageCount,
    IReadOnlyList<string> DocumentIds,
    bool UseDocuments)
{
    public static SessionSummaryDto From(ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt, session.LastActivityAt, session.MessageCount,
            session.DocumentIds, session.UseDocuments);
}

public sealed record SessionDetailDto(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<string> DocumentIds,
    bool UseDocuments)
{
    public static SessionDetailDto From(ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt,
            session.Snapshot().Select(MessageDto.From).ToArray(), session.DocumentIds, session.UseDocuments);
}

public sealed class CreateSessionRequest
{
    /// <summary>Documents to answer from. Omitted or empty = every ready document.</summary>
    public List<string>? DocumentIds { get; set; }

    /// <summary>False = general chat with no document search. Default true.</summary>
    public bool? UseDocuments { get; set; }
}

/// <summary>
/// { documentIds: [...] } answers from those; { documentIds: [] } from every ready
/// document; { documentIds: [], useDocuments: false } is general chat.
/// </summary>
public sealed class SetDocumentsRequest
{
    [Required]
    public List<string> DocumentIds { get; set; } = [];

    public bool UseDocuments { get; set; } = true;
}

public sealed class SendMessageRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "A message is required.")]
    public string Message { get; set; } = "";
}

/// <summary>PromptPlan minus the prompt text itself.</summary>
public sealed record ContextInfoDto(
    int IncludedMessages,
    int DroppedMessages,
    int PromptTokens,
    int ResponseBudget,
    bool ContentTruncated,
    int SourceCount,
    int SourceTokens,
    int DroppedSources);

/// <param name="Content">The stored reply. Can differ from the streamed text: invalid
/// citations removed, missing ones added. The client swaps it in when the stream ends.</param>
public sealed record CompletionInfoDto(
    Guid MessageId,
    int CompletionTokens,
    double ElapsedSeconds,
    double TokensPerSecond,
    IReadOnlyList<int> CitedSources,
    int UncitedSentences,
    string Content,
    double FirstTokenSeconds);

/// <param name="DocumentTitle">Set when exactly one document is ready, for the
/// single-report case; otherwise null and the UI reads /api/documents.</param>
public sealed record ModelStatusDto(
    string Model,
    bool Ready,
    uint ContextSize,
    ulong ParameterCount,
    string? Error,
    bool RagEnabled,
    bool IndexReady,
    string? EmbeddingModel,
    string? DocumentTitle,
    string? IndexError,
    int DocumentCount,
    int PendingDocuments);

// ---- RAG -------------------------------------------------------------------------------

/// <summary>The "sources" SSE event.</summary>
public sealed record SourcesEventDto(bool Grounded, IReadOnlyList<SourceRefDto> Sources);

public sealed record SourceRefDto(
    int N,
    string ChunkId,
    string DocId,
    string DocTitle,
    string Part,
    string PartLabel,
    string Section,
    string? PrintedPages,
    int PdfPageStart,
    int PdfPageEnd,
    string Label,
    string Excerpt,
    float? VectorScore,
    int? Bm25Rank,
    double FusedScore)
{
    public static SourceRefDto From(SourceRef s) =>
        new(s.Number, s.ChunkId, s.DocId, s.DocTitle, s.Part, s.PartLabel, s.Section, s.PrintedPages, s.PdfPageStart,
            s.PdfPageEnd, s.Label, s.Excerpt, s.VectorScore, s.Bm25Rank, s.FusedScore);
}

/// <param name="Stage">While Processing: "Embedding 40/154" and the like.</param>
/// <param name="CanDelete">Uploads can be removed; documents shipped in the source folder can't.</param>
public sealed record DocumentInfoDto(
    string DocId,
    string Title,
    string FileName,
    string Origin,
    string Status,
    string? Stage,
    double? Progress,
    string? Error,
    bool CanDelete,
    DateTimeOffset AddedAt,
    int? PdfPageCount,
    int? ChunkCount,
    string? EmbeddingModel,
    DateTimeOffset? IndexedAt,
    IReadOnlyList<string> ExampleQuestions,
    IReadOnlyList<DocumentPartDto> Parts)
{
    public static DocumentInfoDto From(LibraryDocument d)
    {
        var m = d.Index?.Manifest;
        return new DocumentInfoDto(
            d.DocId, d.Title, d.FileName, d.Origin.ToString(), d.Status.ToString(), d.Stage, d.Progress, d.Error,
            CanDelete: d.Origin == DocumentOrigin.Upload && d.Status != DocumentStatus.Processing,
            d.AddedAt, m?.PdfPageCount, m?.ChunkCount, m?.EmbeddingModel, m?.CreatedAt,
            m?.ExampleQuestions ?? [],
            m?.Parts.Select(p => new DocumentPartDto(p.Name, p.Label, p.FromPage, p.ToPage)).ToArray() ?? []);
    }
}

public sealed record DocumentPartDto(string Name, string Label, int FromPage, int ToPage);

public sealed record ChunkDto(
    string Id,
    string DocId,
    string Kind,
    string Part,
    string PartLabel,
    string Section,
    IReadOnlyList<string> SectionPath,
    int PdfPageStart,
    int PdfPageEnd,
    string? PrintedPages,
    string? Unit,
    string Header,
    string Text);

public sealed class SearchRequest
{
    [Required(AllowEmptyStrings = false)]
    public string Question { get; set; } = "";

    /// <summary>Optional previous user turn, to test follow-up handling.</summary>
    public string? PreviousQuestion { get; set; }

    /// <summary>Documents to search. Omitted = every ready document.</summary>
    public List<string>? DocumentIds { get; set; }
}

public sealed record SearchResultDto(
    bool Grounded,
    float TopVectorScore,
    double ElapsedMilliseconds,
    string VectorQuery,
    string KeywordQuery,
    bool UsedPreviousTurn,
    IReadOnlyList<string> DocumentIds,
    IReadOnlyList<SearchHitDto> Hits);

public sealed record SearchHitDto(
    int N,
    string ChunkId,
    string DocId,
    string Section,
    int PdfPageStart,
    int PdfPageEnd,
    string? PrintedPages,
    float? VectorScore,
    int? VectorRank,
    int? Bm25Rank,
    double FusedScore,
    int Tokens,
    string Text);
