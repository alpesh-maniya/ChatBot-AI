namespace ChatBot.Rag.Retrieval;

// Reciprocal Rank Fusion: score(d) = sum over lists of 1 / (k + rank), rank from 1.
//
// Uses ranks only, which is the point: a cosine of 0.8 and a BM25 score of 14.2 can't be
// added, but "2nd in one list, 5th in the other" can. Ties go to the earlier list, which
// the retriever passes as the vector list.
public static class RrfFusion
{
    public static IReadOnlyList<(int Row, double Score)> Fuse(IReadOnlyList<IReadOnlyList<int>> rankings, int k)
    {
        var scores = new Dictionary<int, double>();
        var firstSeen = new Dictionary<int, (int List, int Rank)>();

        for (var list = 0; list < rankings.Count; list++)
        {
            for (var rank = 0; rank < rankings[list].Count; rank++)
            {
                var row = rankings[list][rank];
                scores[row] = scores.GetValueOrDefault(row) + 1.0 / (k + rank + 1);
                firstSeen.TryAdd(row, (list, rank));
            }
        }

        return scores
            .OrderByDescending(s => s.Value)
            .ThenBy(s => firstSeen[s.Key].List)
            .ThenBy(s => firstSeen[s.Key].Rank)
            .Select(s => (s.Key, s.Value))
            .ToArray();
    }
}
