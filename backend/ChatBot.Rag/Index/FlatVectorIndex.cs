namespace ChatBot.Rag.Index;

// Exact nearest-neighbour search by brute force. Every row is compared with the query;
// with a few hundred 384-dim rows that is ~100k multiply-adds, far below anything an
// approximate index (HNSW etc.) would save.
public sealed class FlatVectorIndex
{
    private readonly float[] _data;
    private readonly int _dims;

    public FlatVectorIndex(float[] data, int dims)
    {
        if (dims <= 0 || data.Length % dims != 0)
        {
            throw new ArgumentException($"{data.Length} floats is not a whole number of {dims}-dim rows.");
        }
        _data = data;
        _dims = dims;
    }

    public int Count => _data.Length / _dims;

    /// <summary>Rows ranked by cosine similarity. Both sides must be normalised.</summary>
    /// <param name="only">When given, rows outside it are not considered.</param>
    public IReadOnlyList<(int Row, float Score)> Search(ReadOnlySpan<float> query, int k, IReadOnlySet<int>? only = null)
    {
        if (query.Length != _dims)
        {
            throw new ArgumentException($"Query has {query.Length} dimensions, index has {_dims}.");
        }

        var scores = new List<(int Row, float Score)>(only?.Count ?? Count);
        for (var row = 0; row < Count; row++)
        {
            if (only is not null && !only.Contains(row)) continue;

            var span = _data.AsSpan(row * _dims, _dims);
            float dot = 0;
            for (var i = 0; i < _dims; i++) dot += span[i] * query[i];
            scores.Add((row, dot));
        }

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scores.Take(k).ToArray();
    }
}
