using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Llm;

// Singleton - the weights are ~1 GB and take seconds to map, so they're loaded once for
// the life of the process.
//
// StatelessExecutor builds a fresh KV cache per request and throws it away, so the
// prompt gets reprocessed every turn (cost is O(conversation), not O(new tokens)). What
// that buys is that one session physically cannot see another's context, because there's
// no retained state to leak. Worth the prefill at this size; see docs/IMPLEMENTATION.md
// if that trade stops making sense.
public sealed class LlamaLanguageModel : ILanguageModel, IAsyncDisposable
{
    private readonly LlamaOptions _options;
    private readonly IChatTemplate _template;
    private readonly ILogger<LlamaLanguageModel> _logger;

    // Inference is CPU-bound and llama.cpp already uses every thread. Two requests at
    // once don't finish sooner, they just each run at half speed and double peak RAM.
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;
    private string? _loadError;

    public LlamaLanguageModel(
        IOptions<LlamaOptions> options,
        IChatTemplate template,
        ILogger<LlamaLanguageModel> logger)
    {
        _options = options.Value;
        _template = template;
        _logger = logger;
    }

    public bool IsReady => _weights is not null && _executor is not null;

    public ModelInfo Info => new(
        Name: Path.GetFileName(_options.ModelPath),
        ContextSize: _options.ContextSize,
        ParameterCount: _weights?.ParameterCount ?? 0,
        Ready: IsReady,
        LoadError: _loadError);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ModelPath))
        {
            _loadError =
                $"Model file not found at '{_options.ModelPath}'. " +
                $"Run scripts/download-model.ps1, or point Llama:ModelPath at an existing .gguf file.";
            _logger.LogError("{Error}", _loadError);
            return;
        }

        try
        {
            _modelParams = new ModelParams(_options.ModelPath)
            {
                ContextSize = _options.ContextSize,
                GpuLayerCount = _options.GpuLayerCount,
                BatchSize = _options.BatchSize,
                // Physical cores. Hyper-threaded siblings share vector units, so going
                // past core count measured slower here, not faster.
                Threads = _options.Threads ?? Math.Max(1, Environment.ProcessorCount / 2)
            };

            var started = System.Diagnostics.Stopwatch.StartNew();
            _weights = await LLamaWeights.LoadFromFileAsync(_modelParams, cancellationToken);
            _executor = new StatelessExecutor(_weights, _modelParams)
            {
                // We render ChatML in ChatMlTemplate; letting LLamaSharp apply the GGUF's
                // own template as well would wrap the prompt twice.
                ApplyTemplate = false
            };

            _logger.LogInformation(
                "Loaded {Model} ({Params:N0} params) in {Elapsed:N1}s, context {Context} tokens, {Threads} threads.",
                Path.GetFileName(_options.ModelPath), _weights.ParameterCount,
                started.Elapsed.TotalSeconds, _options.ContextSize, _modelParams.Threads);
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _logger.LogError(ex, "Failed to load model from {Path}.", _options.ModelPath);
        }
    }

    public int Count(string text)
    {
        var weights = _weights ?? throw new InvalidOperationException("Model is not loaded.");

        // special: true is load-bearing. Without it "<|im_start|>" counts as the ~8 tokens
        // of its literal text instead of the 1 special token it is, and every context
        // calculation drifts high.
        return weights.Tokenize(text, add_bos: true, special: true, Encoding.UTF8).Length;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_executor is null)
        {
            throw new InvalidOperationException(_loadError ?? "Model is not loaded.");
        }

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = _template.StopSequences,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _options.Temperature,
                TopP = _options.TopP,
                TopK = _options.TopK,
                RepeatPenalty = _options.RepeatPenalty,
                Seed = _options.Seed ?? (uint)Random.Shared.Next()
            }
        };

        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            var filter = new StopSequenceFilter(_template.StopSequences);

            await foreach (var chunk in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                var safe = filter.Push(chunk);
                if (safe.Length > 0) yield return safe;
                if (filter.Stopped) yield break;
            }

            var tail = filter.Flush();
            if (tail.Length > 0) yield return tail;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _inferenceGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
