using MemoryMcp.Core.Services;

namespace MemoryMcp.Shared.Service;

/// <summary>An <see cref="IHybridMemorySearch"/> that runs every search on a leased store lane.</summary>
public sealed class PooledHybridMemorySearch : IHybridMemorySearch
{
    private readonly StorePool pool;

    public PooledHybridMemorySearch(StorePool pool)
    {
        this.pool = pool;
    }

    /// <inheritdoc />
    public Task<HybridSearchResult> SearchAsync(HybridSearchRequest request, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.SearchAsync), lane => lane.Search.SearchAsync(request, cancellationToken), cancellationToken);
    }
}
