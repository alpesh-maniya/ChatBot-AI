namespace ChatBot.Rag.Documents;

public enum BlockKind
{
    Heading,
    Paragraph,
    Caption,
    Table,
    OrgChart,
    Glossary
}

// What the page classifier hands the chunker: cleaned text in reading order, with the
// page and section it belongs to. Layout coordinates are gone by this point.
public sealed record SourceBlock
{
    public required BlockKind Kind { get; init; }
    public required string Text { get; init; }
    public required int PdfPage { get; init; }
    public required string Part { get; init; }

    public string? PrintedPage { get; init; }

    /// <summary>1 = running section header, 2 = section heading, 3 = sub-heading. 0 for non-headings.</summary>
    public int HeadingLevel { get; init; }

    /// <summary>Run-in headings ("FY 2018 Financial Results Revenue totaled...") are always level 3.</summary>
    public bool IsRunIn { get; init; }

    public double FontSize { get; init; }

    /// <summary>Table rows rendered as "cell | cell | cell". Empty for prose.</summary>
    public IReadOnlyList<string> Rows { get; init; } = [];

    public string? Title { get; init; }
    public string? Unit { get; init; }
    public IReadOnlyList<string> SectionPath { get; init; } = [];

    /// <summary>Set on an endnote ("Note 18: ...") read from a profile endnote page.</summary>
    public int? NoteNumber { get; init; }
}
