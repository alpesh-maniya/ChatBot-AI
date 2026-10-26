using ChatBot.Api.Llm;

namespace ChatBot.Api.Hosting;

// Loads the weights in the background at startup. Doing it inline would leave the app
// unreachable for the several seconds it takes - no health endpoint, no error, just a
// refused connection - whereas this way /api/model/status can answer the whole time.
public sealed class ModelLoaderService(
    LlamaLanguageModel model,
    ILogger<ModelLoaderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Loading language model...");
        await model.LoadAsync(stoppingToken);

        if (!model.IsReady)
        {
            // Not fatal on purpose: an API that stays up and reports why is easier to
            // diagnose than a process that exits before writing a log.
            logger.LogWarning("Model is not available. Chat requests will return 503 until this is fixed.");
        }
    }
}
