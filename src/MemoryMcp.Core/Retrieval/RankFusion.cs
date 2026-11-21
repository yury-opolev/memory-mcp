namespace MemoryMcp.Core.Retrieval;

/// <summary>How a keyword (BM25) and a dense ranked list are fused.</summary>
public enum FusionMethod
{
    /// <summary>Reciprocal rank fusion with one <c>k</c> and equal weights.</summary>
    Rrf,

    /// <summary>Reciprocal rank fusion with per-list weights.</summary>
    WeightedRrf,

    /// <summary>Convex combination of min-max normalised scores.</summary>
    Convex,
}

/// <summary>Fusion parameters (query time).</summary>
public sealed record FusionSettings
{
    /// <summary>Fusion method.</summary>
    public FusionMethod Method { get; init; } = FusionMethod.Rrf;

    /// <summary>RRF constant <c>k</c>.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Weight of the keyword list (weighted RRF and convex).</summary>
    public double KeywordWeight { get; init; } = 1.0;

    /// <summary>Weight of the dense list (weighted RRF and convex).</summary>
    public double DenseWeight { get; init; } = 1.0;

    /// <summary>Keyword-list weight multiplier for queries with exact intent (BM25 priority).</summary>
    public double ExactIntentKeywordBoost { get; init; } = 2.0;
}

/// <summary>One entry of a ranked list (keyword or dense), best first.</summary>
/// <param name="Key">The ranked item (segment id, memory id, …).</param>
/// <param name="Score">The list's own score (BM25 relevance or cosine similarity); higher is better.</param>
public sealed record RankedItem<TKey>(TKey Key, double Score)
    where TKey : notnull;

/// <summary>An item after fusion.</summary>
/// <param name="Key">The item.</param>
/// <param name="Score">Fused score; higher is better.</param>
/// <param name="KeywordRank">1-based rank in the keyword list, or null.</param>
/// <param name="DenseRank">1-based rank in the dense list, or null.</param>
public sealed record FusedItem<TKey>(TKey Key, double Score, int? KeywordRank, int? DenseRank)
    where TKey : notnull;

/// <summary>
/// Fuses a keyword (BM25) and a dense list: reciprocal rank fusion with one <c>k</c>, weighted RRF, or
/// a convex combination of min-max normalised scores. Queries with exact intent (quoted phrases,
/// identifier-like tokens) multiply the keyword weight, giving the BM25 list priority. Ties prefer the
/// better keyword rank, then the smaller key by <c>tieBreak</c>, so results are deterministic.
/// </summary>
public static class RankFusion
{
    /// <summary>Fuses <paramref name="keyword"/> and <paramref name="dense"/>.</summary>
    public static IReadOnlyList<FusedItem<TKey>> Fuse<TKey>(
        IReadOnlyList<RankedItem<TKey>> keyword,
        IReadOnlyList<RankedItem<TKey>> dense,
        FusionSettings settings,
        bool exactIntent,
        IComparer<TKey> tieBreak)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(dense);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tieBreak);

        var keywordWeight = settings.Method == FusionMethod.Rrf ? 1.0 : settings.KeywordWeight;
        var denseWeight = settings.Method == FusionMethod.Rrf ? 1.0 : settings.DenseWeight;
        if (exactIntent)
        {
            keywordWeight *= settings.ExactIntentKeywordBoost;
        }

        var keywordRanks = Ranks(keyword);
        var denseRanks = Ranks(dense);
        var keywordNorm = settings.Method == FusionMethod.Convex ? Normalised(keyword) : null;
        var denseNorm = settings.Method == FusionMethod.Convex ? Normalised(dense) : null;
        var total = keywordWeight + denseWeight;

        return keywordRanks.Keys.Union(denseRanks.Keys)
            .Select(key =>
            {
                int? kr = keywordRanks.TryGetValue(key, out var k) ? k : null;
                int? dr = denseRanks.TryGetValue(key, out var d) ? d : null;
                double score;
                if (settings.Method == FusionMethod.Convex)
                {
                    score = ((keywordWeight * keywordNorm!.GetValueOrDefault(key)) + (denseWeight * denseNorm!.GetValueOrDefault(key))) / total;
                }
                else
                {
                    score = (kr is { } a ? keywordWeight / (settings.RrfK + a) : 0) + (dr is { } b ? denseWeight / (settings.RrfK + b) : 0);
                }

                return new FusedItem<TKey>(key, score, kr, dr);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.KeywordRank ?? int.MaxValue)
            .ThenBy(c => c.Key, tieBreak)
            .ToList();
    }

    private static Dictionary<TKey, int> Ranks<TKey>(IReadOnlyList<RankedItem<TKey>> list)
        where TKey : notnull
    {
        var ranks = new Dictionary<TKey, int>();
        foreach (var candidate in list)
        {
            ranks.TryAdd(candidate.Key, ranks.Count + 1);
        }

        return ranks;
    }

    private static Dictionary<TKey, double> Normalised<TKey>(IReadOnlyList<RankedItem<TKey>> list)
        where TKey : notnull
    {
        var scores = new Dictionary<TKey, double>();
        if (list.Count == 0)
        {
            return scores;
        }

        var min = list.Min(c => c.Score);
        var max = list.Max(c => c.Score);
        foreach (var candidate in list)
        {
            scores.TryAdd(candidate.Key, max > min ? (candidate.Score - min) / (max - min) : 1.0);
        }

        return scores;
    }
}
