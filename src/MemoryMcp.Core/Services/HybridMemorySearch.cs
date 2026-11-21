using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Services;

/// <summary>One memory search.</summary>
public sealed record HybridSearchRequest
{
    /// <summary>The query text.</summary>
    public required string Query { get; init; }

    /// <summary>The mode to run (callers resolve "auto" to <see cref="MemorySearchOptions.DefaultMode"/>).</summary>
    public required MemorySearchMode Mode { get; init; }

    /// <summary>Maximum number of memories to return.</summary>
    public int Limit { get; init; } = 5;

    /// <summary>Minimum cosine similarity of the semantic list (keyword matches are not thresholded).</summary>
    public float? MinScore { get; init; }

    /// <summary>Tags and excluded ids, applied before scoring in both lists.</summary>
    public MemorySearchFilter Filter { get; init; } = MemorySearchFilter.None;

    /// <summary>Overrides <c>MemoryMcp:Search</c> for this call (evaluation ablations); null = configured.</summary>
    public MemorySearchOptions? Settings { get; init; }
}

/// <summary>Which list found a memory.</summary>
public enum MemoryMatchKind
{
    /// <summary>The semantic (dense) list only.</summary>
    Semantic,

    /// <summary>The keyword (BM25) list only.</summary>
    Keyword,

    /// <summary>Both lists.</summary>
    Both,
}

/// <summary>One returned memory.</summary>
/// <param name="Memory">The memory with its content (<see cref="SearchResult.Score"/> = similarity, 0 for keyword-only hits).</param>
/// <param name="Match">Which list found it.</param>
/// <param name="FusedScore">Rank score after fusion (the similarity in dense mode).</param>
/// <param name="KeywordRank">1-based keyword rank, or null.</param>
/// <param name="DenseRank">1-based semantic rank, or null.</param>
/// <param name="Similarity">Cosine similarity when the semantic list found it, else null.</param>
public sealed record HybridSearchHit(SearchResult Memory, MemoryMatchKind Match, double FusedScore, int? KeywordRank, int? DenseRank, float? Similarity);

/// <summary>Result of one memory search.</summary>
/// <param name="Hits">Memories, best first.</param>
/// <param name="EffectiveMode">The mode that actually ran (after a fallback).</param>
/// <param name="Fallback">Why the requested mode could not run, or null.</param>
/// <param name="TopSimilarity">Best cosine similarity of the semantic list (abstention confidence), or null.</param>
public sealed record HybridSearchResult(IReadOnlyList<HybridSearchHit> Hits, MemorySearchMode EffectiveMode, string? Fallback, float? TopSimilarity);

/// <summary>Curated-memory search in dense, keyword or hybrid mode.</summary>
public interface IHybridMemorySearch
{
    /// <summary>Runs <paramref name="request"/>.</summary>
    Task<HybridSearchResult> SearchAsync(HybridSearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Memory search on the shared engine: the exact semantic list (<see cref="IMemoryStore.SearchDenseAsync"/>)
/// and the BM25 list (<see cref="IMemoryStore.KeywordSearchAsync"/>), both with the filters applied before
/// scoring, fused by <see cref="RankFusion"/> with BM25 priority for exact intent. Dense mode is exactly
/// the previous semantic search (same embedding call, same ranking, embedder errors propagate). Hybrid
/// mode falls back to keyword when the query cannot be embedded, and to dense when the keyword index is
/// unavailable, and says so in <see cref="HybridSearchResult.Fallback"/>.
/// </summary>
public sealed class HybridMemorySearch : IHybridMemorySearch
{
    private readonly IEmbeddingService embedding;
    private readonly IMemoryStore store;
    private readonly IOptionsMonitor<MemoryMcpOptions> options;

    public HybridMemorySearch(IEmbeddingService embedding, IMemoryStore store, IOptionsMonitor<MemoryMcpOptions> options)
    {
        this.embedding = embedding;
        this.store = store;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<HybridSearchResult> SearchAsync(HybridSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Limit);
        var settings = request.Settings ?? this.options.CurrentValue.Search;
        var mode = request.Mode;
        string? fallback = null;

        if (mode != MemorySearchMode.Dense && this.store.KeywordIndexUnavailableReason is not null)
        {
            mode = MemorySearchMode.Dense;
            fallback = "the keyword index is unavailable";
        }

        float[]? vector = null;
        if (mode == MemorySearchMode.Dense)
        {
            vector = await this.EmbedAsync(request.Query, settings, cancellationToken);
            var dense = await this.store.SearchDenseAsync(vector, request.Limit, request.MinScore, request.Filter, cancellationToken);
            var hits = dense.Select((r, i) => new HybridSearchHit(r, MemoryMatchKind.Semantic, r.Score, null, i + 1, r.Score)).ToList();
            return new HybridSearchResult(hits, MemorySearchMode.Dense, fallback, dense.Count > 0 ? dense[0].Score : null);
        }

        if (mode == MemorySearchMode.Hybrid)
        {
            (vector, fallback) = await this.TryEmbedAsync(request.Query, settings, cancellationToken);
            if (vector is null)
            {
                mode = MemorySearchMode.Keyword;
            }
        }

        var match = KeywordQuery.Build(request.Query, settings.KeywordVariant == MemoryKeywordVariant.Stemmed);
        var keyword = match is null ? [] : await this.store.KeywordSearchAsync(match, settings.Candidates, request.Filter, cancellationToken);
        var semantic = vector is null ? [] : await this.store.SearchDenseAsync(vector, settings.Candidates, request.MinScore, request.Filter, cancellationToken);
        var fused = RankFusion.Fuse(
            keyword.Select(k => new RankedItem<string>(k.MemoryId, k.Score)).ToList(),
            semantic.Select(d => new RankedItem<string>(d.MemoryId, d.Score)).ToList(),
            settings.ToFusionSettings(),
            QueryIntent.HasExactIntent(request.Query),
            StringComparer.Ordinal);

        var byId = semantic.ToDictionary(r => r.MemoryId, StringComparer.Ordinal);
        var results = new List<HybridSearchHit>();
        foreach (var item in fused)
        {
            if (results.Count >= request.Limit)
            {
                break;
            }

            var memory = byId.GetValueOrDefault(item.Key) ?? await this.LoadAsync(item.Key, cancellationToken);
            if (memory is null)
            {
                continue; // deleted since the keyword list was read
            }

            var kind = item.KeywordRank is null ? MemoryMatchKind.Semantic
                : item.DenseRank is null ? MemoryMatchKind.Keyword
                : MemoryMatchKind.Both;
            results.Add(new HybridSearchHit(memory, kind, item.Score, item.KeywordRank, item.DenseRank, item.DenseRank is null ? null : memory.Score));
        }

        return new HybridSearchResult(results, mode, fallback, semantic.Count > 0 ? semantic[0].Score : null);
    }

    private Task<float[]> EmbedAsync(string query, MemorySearchOptions settings, CancellationToken cancellationToken) =>
        settings.EffectiveQueryInstruction() is { } instruction
            ? this.embedding.EmbedQueryAsync(query, instruction, cancellationToken)
            : this.embedding.EmbedAsync(query, cancellationToken);

    private async Task<(float[]? Vector, string? Fallback)> TryEmbedAsync(string query, MemorySearchOptions settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.QueryTimeoutMs);
        try
        {
            return (await this.EmbedAsync(query, settings, timeout.Token), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "the query embedding timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return (null, "the query embedding failed");
        }
    }

    private async Task<SearchResult?> LoadAsync(string memoryId, CancellationToken cancellationToken)
    {
        var memory = await this.store.GetMemoryAsync(memoryId, cancellationToken);
        return memory is null ? null : new SearchResult
        {
            MemoryId = memory.MemoryId,
            Title = memory.Title,
            Content = memory.Content,
            Tags = memory.Tags,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            Score = 0,
        };
    }
}
