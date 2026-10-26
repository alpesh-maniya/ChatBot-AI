using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatBot.Rag.Embeddings;

// bge-small-en-v1.5 through LLamaSharp, the same runtime as the chat model, so there is
// nothing new to install. ~36 MB at Q8_0; one embedding is a single forward pass of a
// 33M-parameter encoder, a few milliseconds on CPU.
//
// LLamaEmbedder owns one llama context, which isn't safe to share between threads, so
// calls are serialised. A query takes milliseconds, so the queue never builds up.
public sealed class LlamaEmbeddingModel : IEmbeddingModel, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;

    public LlamaEmbeddingModel(EmbeddingOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => Path.GetFileName(_options.ModelPath);
    public int Dimensions => _embedder?.EmbeddingSize ?? 0;
    public bool IsReady => _embedder is not null;
    public string? LoadError { get; private set; }

    /// <summary>Longest input in tokens. Anything longer would be cut, so ingestion refuses it.</summary>
    public int ContextLimit => (int)_options.ContextSize;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ModelPath))
        {
            LoadError = $"Embedding model not found at '{_options.ModelPath}'. Run scripts/download-model.ps1 -Model bge-small.";
            _logger.LogError("{Error}", LoadError);
            return;
        }

        try
        {
            var parameters = new ModelParams(_options.ModelPath)
            {
                ContextSize = _options.ContextSize,
                // An encoder sees the whole input in one batch; both sizes must cover it.
                BatchSize = _options.ContextSize,
                UBatchSize = _options.ContextSize,
                Embeddings = true,
                PoolingType = LLamaPoolingType.CLS,
                GpuLayerCount = 0,
                Threads = _options.Threads ?? Math.Max(1, Environment.ProcessorCount / 2)
            };

            _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
            _embedder = new LLamaEmbedder(_weights, parameters);

            _logger.LogInformation("Loaded embedding model {Model}, {Dims} dimensions.", Name, _embedder.EmbeddingSize);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            _logger.LogError(ex, "Failed to load embedding model from {Path}.", _options.ModelPath);
        }
    }

    public int CountTokens(string text)
    {
        var weights = _weights ?? throw new InvalidOperationException("Embedding model is not loaded.");
        return weights.Tokenize(text, add_bos: true, special: true, Encoding.UTF8).Length;
    }

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken) =>
        EmbedAsync(_options.DocumentPrefix + text, cancellationToken);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) =>
        EmbedAsync(_options.QueryPrefix + text, cancellationToken);

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedder = _embedder ?? throw new InvalidOperationException(LoadError ?? "Embedding model is not loaded.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var vectors = await embedder.GetEmbeddings(text, cancellationToken);
            return Normalize(vectors[0]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += v * v;
        var norm = (float)Math.Sqrt(sum);
        if (norm == 0) return vector;

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
        _gate.Dispose();
    }
}
