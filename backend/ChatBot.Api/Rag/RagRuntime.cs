using System.Collections.Concurrent;
using System.Threading.Channels;
using ChatBot.Rag.Documents;
using ChatBot.Rag.Embeddings;
using ChatBot.Rag.Index;
using ChatBot.Rag.Ingestion;
using ChatBot.Rag.Retrieval;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Rag;

/// <summary>The documents a question is answered from, plus the prompt text filled for them.</summary>
public sealed record RagScope(
    IReadOnlyList<LibraryDocument> Documents,
    HybridRetriever Retriever,
    string SystemPrompt,
    string AnswerInstruction,
    string FallbackMessage);

// Owns the embedding model and every document's index for the life of the process.
//
// At startup it scans the source and upload folders. A PDF whose index on disk matches
// it (same file hash, same embedder, same chunker version) loads straight away; any other
// is queued for the ingestion worker. So dropping a PDF into data/source and restarting,
// or uploading one, is all it takes to add a document.
public sealed class RagRuntime(IOptions<RagOptions> options, ILogger<RagRuntime> logger) : IDisposable
{
    private readonly RagOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, LibraryDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private LlamaEmbeddingModel? _embedder;
    private volatile bool _scanned;

    public bool Enabled => _options.Enabled;
    public RagOptions Options => _options;
    public string? LoadError { get; private set; }

    /// <summary>The embedder is up and the folders have been scanned. Says nothing about
    /// whether any one document is ready; see <see cref="LibraryDocument.Status"/>.</summary>
    public bool IsReady => _embedder is { IsReady: true } && _scanned;

    public string EmbeddingModelName => Path.GetFileName(_options.Embedding.ModelPath);

    internal ChannelReader<string> Queue => _queue.Reader;

    public IReadOnlyList<LibraryDocument> Documents =>
        _documents.Values.OrderBy(d => d.Origin).ThenBy(d => d.AddedAt).ThenBy(d => d.DocId).ToArray();

    public LibraryDocument? Find(string docId) => _documents.GetValueOrDefault(docId);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        try
        {
            _embedder = new LlamaEmbeddingModel(_options.Embedding, logger);
            await _embedder.LoadAsync(cancellationToken);
            if (!_embedder.IsReady)
            {
                LoadError = _embedder.LoadError;
                return;
            }

            Directory.CreateDirectory(_options.IndexFolder);
            Directory.CreateDirectory(_options.UploadFolder);

            await ScanAsync(_options.SourceFolder, DocumentOrigin.Library, cancellationToken);
            await ScanAsync(_options.UploadFolder, DocumentOrigin.Upload, cancellationToken);
            _scanned = true;

            var ready = _documents.Values.Count(d => d.Status == DocumentStatus.Ready);
            logger.LogInformation("RAG ready: {Ready} document(s) loaded, {Queued} queued for indexing.",
                ready, _documents.Count - ready);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            logger.LogError(ex, "Failed to load the document library.");
        }
    }

    private async Task ScanAsync(string folder, DocumentOrigin origin, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Document folder {Folder} does not exist.", folder);
            return;
        }

        // Two copies of the same PDF load once, so the copy with a profile has to be seen
        // first: "report (2).pdf" sorts before "report.pdf" and would otherwise take the
        // hand-tuned report's place with default rules.
        var pdfs = Directory.EnumerateFiles(folder, "*.pdf")
            .OrderBy(p => IngestProfile.FindFor(p) is null)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var pdf in pdfs)
        {
            try
            {
                var sha = await IngestPipeline.Sha256Async(pdf, cancellationToken);
                if (_documents.Values.Any(d => d.Sha256 == sha))
                {
                    logger.LogInformation("Skipping {File}: same content as a document already loaded.", Path.GetFileName(pdf));
                    continue;
                }

                var profile = IngestProfile.FindFor(pdf) ?? IngestPipeline.DefaultProfile(pdf, sha);
                var document = new LibraryDocument
                {
                    DocId = profile.DocId,
                    PdfPath = pdf,
                    Sha256 = sha,
                    Origin = origin,
                    Profile = profile,
                    AddedAt = File.GetCreationTimeUtc(pdf)
                };

                if (!_documents.TryAdd(document.DocId, document))
                {
                    logger.LogWarning("Skipping {File}: document id {DocId} is already taken.", Path.GetFileName(pdf), document.DocId);
                    continue;
                }

                var stale = WhyStale(document);
                if (stale is null)
                {
                    document.MarkReady(RagIndex.Load(IndexFolderOf(document), profile));
                    logger.LogInformation("Loaded {Title} ({DocId}, {Chunks} chunks).",
                        document.Title, document.DocId, document.Index!.Chunks.Count);
                }
                else
                {
                    logger.LogInformation("Queued {File} for indexing: {Reason}.", Path.GetFileName(pdf), stale);
                    Enqueue(document, stale);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not register {File}.", Path.GetFileName(pdf));
            }
        }
    }

    // Null when the index on disk is current for this PDF. An index built with another
    // embedder has vectors in a different space, and one from an older chunker has
    // different passages; both are rebuilt rather than served.
    private string? WhyStale(LibraryDocument document)
    {
        var manifest = IndexStore.ReadManifest(IndexFolderOf(document));
        if (manifest is null) return "no index yet";
        if (!string.Equals(manifest.SourceSha256, document.Sha256, StringComparison.OrdinalIgnoreCase)) return "the PDF changed";
        if (!string.Equals(manifest.EmbeddingModel, _embedder!.Name, StringComparison.OrdinalIgnoreCase)
            || manifest.EmbeddingDimensions != _embedder.Dimensions)
        {
            return $"built with {manifest.EmbeddingModel}, now using {_embedder.Name}";
        }
        if (manifest.ChunkerVersion < DocumentManifest.CurrentChunkerVersion)
        {
            return $"chunker v{manifest.ChunkerVersion}, current is v{DocumentManifest.CurrentChunkerVersion}";
        }
        return null;
    }

    private string IndexFolderOf(LibraryDocument document) => Path.Combine(_options.IndexFolder, document.DocId);

    private void Enqueue(LibraryDocument document, string? reason = null)
    {
        document.MarkQueued(reason);
        _queue.Writer.TryWrite(document.DocId);
    }

    // ---- ingestion (called by the worker, one document at a time) ----------------------

    internal async Task IngestAsync(string docId, CancellationToken cancellationToken)
    {
        if (Find(docId) is not { } document || document.Status != DocumentStatus.Queued) return;
        var embedder = _embedder ?? throw new InvalidOperationException("Embedding model is not loaded.");

        document.MarkProcessing("Starting");
        try
        {
            // Not Progress<T>: it posts to the thread pool, and a late "Writing the index"
            // could land after MarkReady and flip the document back to Processing.
            var progress = new InlineProgress(p => document.MarkProcessing(p.Message, Fraction(p)));
            var result = await new IngestPipeline(embedder).RunAsync(
                document.PdfPath, document.Profile, IndexFolderOf(document), progress, cancellationToken);

            document.MarkReady(RagIndex.Load(IndexFolderOf(document), document.Profile));
            logger.LogInformation("Indexed {Title} ({DocId}): {Chunks} chunks from {Pages} pages in {Seconds:N1}s.",
                document.Title, docId, result.Chunks.Count, result.PagesRead, result.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            document.MarkQueued("interrupted by shutdown");
            throw;
        }
        catch (Exception ex)
        {
            document.MarkFailed(ex is InvalidDataException ? ex.Message : $"Indexing failed: {ex.Message}");
            logger.LogError(ex, "Indexing {DocId} failed.", docId);
        }
    }

    // Embedding is ~90% of the time for any document worth indexing, so it gets most of
    // the bar; the other stages are a few seconds at most.
    private static double Fraction(IngestProgress p) => p.Stage switch
    {
        IngestStage.Reading => 0.02,
        IngestStage.Classifying => 0.06,
        IngestStage.Chunking => 0.08,
        IngestStage.Embedding when p.Total > 0 => 0.1 + 0.88 * p.Done / p.Total,
        IngestStage.Embedding => 0.1,
        _ => 0.99
    };

    // ---- uploads -----------------------------------------------------------------------

    /// <summary>Stores the PDF and queues it. Uploading a file the library already has
    /// returns that document instead of indexing it twice.</summary>
    public async Task<LibraryDocument> AddUploadAsync(Stream content, string originalName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.UploadFolder);
        var temp = Path.Combine(_options.UploadFolder, $".upload-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var file = File.Create(temp))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            if (!await StartsWithPdfHeaderAsync(temp, cancellationToken))
            {
                throw new InvalidDataException("That file is not a PDF.");
            }

            var sha = await IngestPipeline.Sha256Async(temp, cancellationToken);

            // Serialised so two uploads of the same file can't both pass the duplicate check.
            await _uploadGate.WaitAsync(cancellationToken);
            try
            {
                if (_documents.Values.FirstOrDefault(d => d.Sha256 == sha) is { } existing)
                {
                    if (existing.Status == DocumentStatus.Failed) Enqueue(existing, "retry after re-upload");
                    return existing;
                }

                var target = UniquePath(_options.UploadFolder, SafeFileName(originalName));
                File.Move(temp, target);

                IngestProfile profile;
                try
                {
                    profile = IngestPipeline.DefaultProfile(target, sha);
                }
                catch (Exception ex)
                {
                    File.Delete(target);
                    throw new InvalidDataException($"The PDF could not be opened: {ex.Message}");
                }

                var document = new LibraryDocument
                {
                    DocId = profile.DocId,
                    PdfPath = target,
                    Sha256 = sha,
                    Origin = DocumentOrigin.Upload,
                    Profile = profile,
                    AddedAt = DateTimeOffset.UtcNow
                };
                _documents[document.DocId] = document;
                Enqueue(document);

                logger.LogInformation("Uploaded {File} as {DocId} ({Pages} pages); queued for indexing.",
                    document.FileName, document.DocId, profile.Parts[0].ToPage);
                return document;
            }
            finally
            {
                _uploadGate.Release();
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Uploaded documents only, and not while their index is being written.</summary>
    public RemoveResult Remove(string docId)
    {
        if (Find(docId) is not { } document) return RemoveResult.NotFound;
        if (document.Origin != DocumentOrigin.Upload) return RemoveResult.NotAllowed;
        if (document.Status == DocumentStatus.Processing) return RemoveResult.Busy;

        _documents.TryRemove(docId, out _);
        TryDelete(() => Directory.Delete(IndexFolderOf(document), recursive: true), IndexFolderOf(document));
        TryDelete(() => File.Delete(document.PdfPath), document.PdfPath);
        logger.LogInformation("Removed {DocId} ({File}).", docId, document.FileName);
        return RemoveResult.Removed;
    }

    private void TryDelete(Action delete, string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) delete();
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete {Path}; remove it by hand.", path);
        }
    }

    // ---- answering ---------------------------------------------------------------------

    /// <summary>
    /// Whether a conversation goes through retrieval at all. Not in general-chat mode, and
    /// not while the library is empty - with nothing to search, refusing to chat would
    /// just be unhelpful.
    /// </summary>
    public bool AnswersFromDocuments(bool useDocuments, IReadOnlyList<string> attached) =>
        _options.Enabled && useDocuments && (attached.Count > 0 || !_documents.IsEmpty);

    /// <summary>
    /// The documents a conversation is answered from: the ones attached to it, or every
    /// ready document when nothing is attached. Null plus a reason when that set can't be
    /// searched yet.
    /// </summary>
    public (RagScope? Scope, string? Problem) ResolveScope(IReadOnlyList<string> attached)
    {
        if (_embedder is not { IsReady: true } embedder || !_scanned)
        {
            return (null, LoadError ?? "The document library is still loading. Try again in a moment.");
        }

        List<LibraryDocument> documents;
        if (attached.Count > 0)
        {
            var found = attached.Select(Find).OfType<LibraryDocument>().ToList();
            if (found.Count == 0) return (null, "The documents attached to this conversation were removed. Attach another one.");

            if (found.FirstOrDefault(d => d.Status is DocumentStatus.Queued or DocumentStatus.Processing) is { } pending)
            {
                return (null, $"\"{pending.Title}\" is still being indexed ({pending.Stage ?? "queued"}). Try again in a moment.");
            }

            documents = found.Where(d => d.Status == DocumentStatus.Ready).ToList();
            if (documents.Count == 0)
            {
                return (null, $"\"{found[0].Title}\" could not be indexed: {found[0].Error}");
            }
        }
        else
        {
            documents = Documents.Where(d => d.Status == DocumentStatus.Ready).ToList();
            if (documents.Count == 0)
            {
                return (null, _documents.IsEmpty
                    ? "No documents are indexed yet. Attach a PDF to start."
                    : "The documents are still being indexed. Try again in a moment.");
            }
        }

        var retriever = new HybridRetriever(documents.Select(d => d.Index!).ToArray(), embedder, _options.Retrieval);
        var (system, instruction, fallback) = FillPrompts(_options,
            documents.Select(d => (d.Title, d.Index!.Manifest.Subject)).ToArray());
        return (new RagScope(documents, retriever, system, instruction, fallback), null);
    }

    // With the GPO report alone in scope these come out word for word as the fixed
    // prompts did before, which keeps its answers (and the eval baseline) comparable.
    internal static (string System, string Instruction, string Fallback) FillPrompts(
        RagOptions options,
        IReadOnlyList<(string Title, string? Subject)> documents)
    {
        static string The(string title) => title.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? title : "the " + title;

        var subject = documents.Count == 1
            ? documents[0].Subject ?? The(documents[0].Title)
            : "these documents: " + string.Join("; ", documents.Select(d => The(d.Title)));
        var names = documents.Count == 1 ? The(documents[0].Title) : "the selected documents";

        string Fill(string template, string fallbackText) => template
            .Replace("{subject}", subject)
            .Replace("{documents}", names)
            .Replace("{fallback}", fallbackText);

        var fallback = Fill(options.FallbackMessage, "");
        return (Fill(options.SystemPrompt, fallback), Fill(options.AnswerInstruction, fallback), fallback);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static async Task<bool> StartsWithPdfHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(header, cancellationToken);
        return read == 5 && header.SequenceEqual("%PDF-"u8.ToArray());
    }

    private static string SafeFileName(string name)
    {
        var file = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        file = file.Trim().TrimStart('.');
        if (file.Length == 0) file = "document";
        if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) file += ".pdf";
        return file.Length > 120 ? file[..116] + ".pdf" : file;
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}).pdf");
        }
        return path;
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        _uploadGate.Dispose();
    }
}

internal sealed class InlineProgress(Action<IngestProgress> report) : IProgress<IngestProgress>
{
    public void Report(IngestProgress value) => report(value);
}

public enum RemoveResult
{
    Removed,
    NotFound,
    NotAllowed,
    Busy
}
