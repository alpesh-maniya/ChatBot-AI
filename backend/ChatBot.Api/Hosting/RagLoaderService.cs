using ChatBot.Api.Rag;

namespace ChatBot.Api.Hosting;

// Same pattern as ModelLoaderService: load in the background, keep the API up and
// reporting status even if something is missing. Then it stays on as the ingestion
// worker, indexing queued documents one at a time - two at once would only split the
// same CPU between them.
public sealed class RagLoaderService(RagRuntime rag, ILogger<RagLoaderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!rag.Enabled)
        {
            logger.LogInformation("RAG is disabled (Rag:Enabled = false); answering from the model alone.");
            return;
        }

        logger.LogInformation("Loading the embedding model and document library...");
        await rag.LoadAsync(stoppingToken);

        if (!rag.IsReady)
        {
            logger.LogWarning("RAG is not available: {Error}. Chat requests will return 503 until this is fixed.", rag.LoadError);
            return;
        }

        try
        {
            await foreach (var docId in rag.Queue.ReadAllAsync(stoppingToken))
            {
                await rag.IngestAsync(docId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. A half-written index stays in its .tmp folder and the PDF is
            // queued again on the next start.
        }
    }
}
