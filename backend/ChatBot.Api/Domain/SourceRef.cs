namespace ChatBot.Api.Domain;

// One numbered source behind an answer: what [n] in the reply points at. Stored on the
// assistant message so reopening a conversation still shows where each answer came from.
public sealed record SourceRef(
    int Number,
    string ChunkId,
    string DocId,
    string Part,
    string PartLabel,
    string Section,
    string? PrintedPages,
    int PdfPageStart,
    int PdfPageEnd,
    string Excerpt,
    float? VectorScore,
    int? Bm25Rank,
    double FusedScore,
    string DocTitle = "")
{
    /// <summary>"GPO's Finances > FY 2018 Financial Results, Annual Report p. 23"</summary>
    public string Label
    {
        get
        {
            var page = PrintedPages is null ? $"PDF p. {PdfPageStart}" : ChatBot.Rag.Ingestion.PartRange.PageText(PartLabel, PrintedPages);
            return Section == PartLabel ? page : $"{Section}, {page}";
        }
    }
}
