using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Rag.Documents;

namespace ChatBot.Rag.Index;

// The index is a folder of plain files - no database:
//
//   manifest.json   what was indexed, with which model and chunker settings
//   chunks.jsonl    one DocumentChunk per line, in ordinal order
//   vectors.f32     float32 little-endian, row i = chunk i, already L2-normalised
//   acronyms.json   acronym -> expansion, for keyword query expansion
//
// For one annual report that is ~200 chunks and well under a megabyte of vectors, which
// brute-force search handles in microseconds.
public static class IndexStore
{
    public const string ManifestFile = "manifest.json";
    public const string ChunksFile = "chunks.jsonl";
    public const string VectorsFile = "vectors.f32";
    public const string AcronymsFile = "acronyms.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(
        string folder,
        DocumentManifest manifest,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> vectors,
        IReadOnlyDictionary<string, string> acronyms)
    {
        if (chunks.Count != vectors.Count)
        {
            throw new ArgumentException($"{chunks.Count} chunks but {vectors.Count} vectors.");
        }

        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, ManifestFile), JsonSerializer.Serialize(manifest, Json));
        File.WriteAllLines(Path.Combine(folder, ChunksFile), chunks.Select(c => JsonSerializer.Serialize(c, JsonLine)));
        File.WriteAllText(Path.Combine(folder, AcronymsFile),
            JsonSerializer.Serialize(acronyms.OrderBy(a => a.Key).ToDictionary(a => a.Key, a => a.Value), Json));

        using var stream = File.Create(Path.Combine(folder, VectorsFile));
        using var writer = new BinaryWriter(stream);
        foreach (var vector in vectors)
        {
            if (vector.Length != manifest.EmbeddingDimensions)
            {
                throw new ArgumentException($"Vector has {vector.Length} dimensions, manifest says {manifest.EmbeddingDimensions}.");
            }
            foreach (var v in vector) writer.Write(v);
        }
    }

    /// <summary>Just manifest.json, to decide whether an index is current without loading it. Null if absent.</summary>
    public static DocumentManifest? ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<DocumentManifest>(File.ReadAllText(path), Json) : null;
    }

    public static LoadedIndex Read(string folder)
    {
        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"No index at '{folder}'. Run ChatBot.Ingest to build it.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<DocumentManifest>(File.ReadAllText(manifestPath), Json)
                       ?? throw new InvalidDataException("manifest.json is empty.");

        var chunks = File.ReadLines(Path.Combine(folder, ChunksFile))
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<DocumentChunk>(l, JsonLine)!)
            .ToArray();

        var bytes = File.ReadAllBytes(Path.Combine(folder, VectorsFile));
        var dims = manifest.EmbeddingDimensions;
        var expected = (long)chunks.Length * dims * sizeof(float);
        if (bytes.LongLength != expected)
        {
            throw new InvalidDataException(
                $"vectors.f32 is {bytes.LongLength} bytes; {chunks.Length} chunks x {dims} dims needs {expected}. Re-run ingestion.");
        }

        var vectors = new float[chunks.Length * dims];
        Buffer.BlockCopy(bytes, 0, vectors, 0, bytes.Length);

        var acronymsPath = Path.Combine(folder, AcronymsFile);
        var acronyms = File.Exists(acronymsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(acronymsPath), Json) ?? []
            : [];

        return new LoadedIndex(manifest, chunks, vectors, acronyms);
    }
}

public sealed record LoadedIndex(
    DocumentManifest Manifest,
    IReadOnlyList<DocumentChunk> Chunks,
    float[] Vectors,
    IReadOnlyDictionary<string, string> Acronyms);
