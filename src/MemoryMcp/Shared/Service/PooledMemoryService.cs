using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;

namespace MemoryMcp.Shared.Service;

/// <summary>An <see cref="IMemoryService"/> that runs every call on a leased store lane.</summary>
public sealed class PooledMemoryService : IMemoryService
{
    private readonly StorePool pool;

    public PooledMemoryService(StorePool pool)
    {
        this.pool = pool;
    }

    /// <inheritdoc />
    public Task<IngestResult> IngestAsync(string content, string? title = null, List<string>? tags = null, bool force = false, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.IngestAsync), lane => lane.Memory.IngestAsync(content, title, tags, force, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<MemoryResult?> GetAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.GetAsync), lane => lane.Memory.GetAsync(memoryId, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<MemoryResult?> UpdateAsync(string memoryId, string? content = null, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.UpdateAsync), lane => lane.Memory.UpdateAsync(memoryId, content, title, tags, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.DeleteAsync), lane => lane.Memory.DeleteAsync(memoryId, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteManyAsync(IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.DeleteManyAsync), lane => lane.Memory.DeleteManyAsync(memoryIds, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<List<SearchResult>> SearchAsync(string query, int limit = 5, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.SearchAsync), lane => lane.Memory.SearchAsync(query, limit, minScore, tags, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<List<SearchResult>> FindSimilarAsync(string text, int limit, float minScore, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.FindSimilarAsync), lane => lane.Memory.FindSimilarAsync(text, limit, minScore, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<List<SearchResult>> FindSimilarAsync(float[] vector, int limit, float minScore, CancellationToken cancellationToken = default)
    {
        return this.pool.RunAsync(nameof(this.FindSimilarAsync), lane => lane.Memory.FindSimilarAsync(vector, limit, minScore, cancellationToken), cancellationToken);
    }
}
