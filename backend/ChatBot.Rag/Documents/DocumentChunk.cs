using System.Text.Json.Serialization;

namespace ChatBot.Rag.Documents;

public enum ChunkKind
{
    Text,
    Table,
    OrgChart,
    Glossary
}

// The unit of retrieval. One line of chunks.jsonl, one row of vectors.f32.
public sealed record DocumentChunk
{
    /// <summary>"gpo-ar-fy2018:0042" - docId plus ordinal, so ids are stable across re-ingests.</summary>
    public required string Id { get; init; }
    public required string DocId { get; init; }
    public required int Ordinal { get; init; }
    public required ChunkKind Kind { get; init; }

    /// <summary>"AnnualReport" or "FinancialStatements". The two parts restart their page numbering.</summary>
    public required string Part { get; init; }
    public required string PartLabel { get; init; }

    public required IReadOnlyList<string> SectionPath { get; init; }
    public required int PdfPageStart { get; init; }
    public required int PdfPageEnd { get; init; }

    /// <summary>As printed in the page footer, e.g. "23" or "23-24".</summary>
    public string? PrintedPages { get; init; }

    /// <summary>"in thousands" for the financial tables. Repeated in the header so the model sees it.</summary>
    public string? Unit { get; init; }

    public required string Header { get; init; }
    public required string Text { get; init; }
    public required int TokenCount { get; init; }
    public required string ContentHash { get; init; }

    // What gets embedded and keyword-indexed. The header carries the section and page
    // that the paragraph itself usually leaves out.
    [JsonIgnore]
    public string IndexText => Header + "\n" + Text;

    [JsonIgnore]
    public string Section => SectionPath.Count == 0 ? PartLabel : string.Join(" > ", SectionPath);
}
