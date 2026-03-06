using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Orchestrates chunking, embedding, and storage for memory operations.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IChunkingService _chunking;
    private readonly IEmbeddingService _embedding;
    private readonly IMemoryStore _store;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        IChunkingService chunking,
        IEmbeddingService embedding,
        IMemoryStore store,
        ILogger<MemoryService> logger)
    {
        _chunking = chunking;
        _embedding = embedding;
        _store = store;
        _logger = logger;
    }

    public async Task<string> IngestAsync(string content, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty.", nameof(content));

        var memoryId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var resolvedTags = tags ?? [];

        // Chunk the content
        var chunkInfos = _chunking.Chunk(content);
        _logger.LogDebug("Memory {MemoryId}: content split into {ChunkCount} chunks.", memoryId, chunkInfos.Count);

        // Embed all chunks in batch
        var chunkTexts = chunkInfos.Select(c => c.Text).ToList();
        var vectors = await _embedding.EmbedBatchAsync(chunkTexts, cancellationToken);

        // Build chunk records
        var chunks = chunkInfos.Select(ci => new ChunkRecord
        {
            MemoryId = memoryId,
            ChunkIndex = ci.ChunkIndex,
            StartOffset = ci.StartOffset,
            Length = ci.Length,
            Title = title,
            Tags = resolvedTags,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();

        // Store everything
        await _store.StoreMemoryAsync(memoryId, content, chunks, vectors.ToList(), cancellationToken);

        _logger.LogInformation("Ingested memory {MemoryId} with {ChunkCount} chunks.", memoryId, chunks.Count);
        return memoryId;
    }

    public async Task<MemoryResult?> GetAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        return await _store.GetMemoryAsync(memoryId, cancellationToken);
    }

    public async Task<MemoryResult?> UpdateAsync(string memoryId, string? content = null, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (!await _store.ExistsAsync(memoryId, cancellationToken))
            return null;

        if (content is not null)
        {
            // Content changed: need to re-chunk and re-embed.
            // Get existing metadata to preserve title/tags if not being updated.
            var existing = await _store.GetMemoryAsync(memoryId, cancellationToken);
            if (existing is null)
                return null;

            var resolvedTitle = title ?? existing.Title;
            var resolvedTags = tags ?? existing.Tags;
            var now = DateTimeOffset.UtcNow;

            // Delete old chunks and vectors
            await _store.DeleteMemoryAsync(memoryId, cancellationToken);

            // Re-chunk
            var chunkInfos = _chunking.Chunk(content);
            _logger.LogDebug("Memory {MemoryId}: re-chunked into {ChunkCount} chunks.", memoryId, chunkInfos.Count);

            // Re-embed
            var chunkTexts = chunkInfos.Select(c => c.Text).ToList();
            var vectors = await _embedding.EmbedBatchAsync(chunkTexts, cancellationToken);

            // Build new chunk records
            var chunks = chunkInfos.Select(ci => new ChunkRecord
            {
                MemoryId = memoryId,
                ChunkIndex = ci.ChunkIndex,
                StartOffset = ci.StartOffset,
                Length = ci.Length,
                Title = resolvedTitle,
                Tags = resolvedTags,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
            }).ToList();

            // Store
            await _store.StoreMemoryAsync(memoryId, content, chunks, vectors.ToList(), cancellationToken);
            _logger.LogInformation("Updated memory {MemoryId} content with {ChunkCount} chunks.", memoryId, chunks.Count);
        }
        else
        {
            // Metadata-only update (title and/or tags)
            var now = DateTimeOffset.UtcNow;
            await _store.UpdateMetadataAsync(memoryId, title, tags, now, cancellationToken);
            _logger.LogInformation("Updated memory {MemoryId} metadata.", memoryId);
        }

        return await _store.GetMemoryAsync(memoryId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        var deleted = await _store.DeleteMemoryAsync(memoryId, cancellationToken);
        if (deleted)
            _logger.LogInformation("Deleted memory {MemoryId}.", memoryId);
        return deleted;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 5, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        // Embed the query
        var queryVector = await _embedding.EmbedAsync(query, cancellationToken);

        // Search
        return await _store.SearchAsync(queryVector, limit, minScore, tags, cancellationToken);
    }
}
