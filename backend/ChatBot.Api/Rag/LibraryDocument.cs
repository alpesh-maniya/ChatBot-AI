using ChatBot.Rag.Ingestion;
using ChatBot.Rag.Retrieval;

namespace ChatBot.Api.Rag;

public enum DocumentStatus
{
    Queued,
    Processing,
    Ready,
    Failed
}

public enum DocumentOrigin
{
    /// <summary>Shipped in Rag:SourceFolder. Can't be deleted through the API.</summary>
    Library,

    /// <summary>Uploaded by a user into Rag:UploadFolder.</summary>
    Upload
}

// One PDF the API knows about, whatever state its index is in. Status fields are written
// by the ingestion worker and read by request threads, hence the lock; the index itself
// is swapped in as a whole and never mutated.
public sealed class LibraryDocument
{
    private readonly Lock _gate = new();
    private DocumentStatus _status = DocumentStatus.Queued;
    private string? _stage;
    private double? _progress;
    private string? _error;
    private RagIndex? _index;

    public required string DocId { get; init; }
    public required string PdfPath { get; init; }
    public required string Sha256 { get; init; }
    public required DocumentOrigin Origin { get; init; }
    public required IngestProfile Profile { get; init; }
    public required DateTimeOffset AddedAt { get; init; }

    public string FileName => Path.GetFileName(PdfPath);
    public string Title => Index?.Manifest.Title ?? Profile.Title;

    public DocumentStatus Status { get { lock (_gate) return _status; } }
    public string? Stage { get { lock (_gate) return _stage; } }

    /// <summary>0..1 while Processing, null otherwise.</summary>
    public double? Progress { get { lock (_gate) return _progress; } }
    public string? Error { get { lock (_gate) return _error; } }
    public RagIndex? Index { get { lock (_gate) return _index; } }

    public void MarkQueued(string? reason = null)
    {
        lock (_gate) (_status, _stage, _progress, _error) = (DocumentStatus.Queued, reason, null, null);
    }

    public void MarkProcessing(string stage, double progress = 0)
    {
        lock (_gate) (_status, _stage, _progress) = (DocumentStatus.Processing, stage, Math.Clamp(progress, 0, 1));
    }

    public void MarkReady(RagIndex index)
    {
        lock (_gate) (_status, _stage, _progress, _error, _index) = (DocumentStatus.Ready, null, null, null, index);
    }

    public void MarkFailed(string error)
    {
        lock (_gate) (_status, _stage, _progress, _error) = (DocumentStatus.Failed, null, null, error);
    }
}
