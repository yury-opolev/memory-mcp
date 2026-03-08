using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Orchestrates chunking, embedding, and storage for memory operations.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IChunkingService chunking;
    private readonly IEmbeddingService embedding;
    private readonly IMemoryStore store;
    private readonly MemoryMcpOptions options;
    private readonly ILogger<MemoryService> logger;

    public MemoryService(
        IChunkingService chunking,
        IEmbeddingService embedding,
        IMemoryStore store,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryService> logger)
    {
        this.chunking = chunking;
        this.embedding = embedding;
        this.store = store;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<IngestResult> IngestAsync(string content, string? title = null, List<string>? tags = null, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content cannot be empty.", nameof(content));
        }

        float[]? contentVector = null;

        // Duplicate guard: check for near-identical existing memories
        if (!force && this.options.DuplicateThreshold > 0)
        {
            contentVector = await this.embedding.EmbedAsync(content, cancellationToken);
            var similar = await this.store.SearchAsync(
                contentVector,
                limit: this.options.DuplicateSearchLimit,
                minScore: this.options.DuplicateThreshold,
                tags: null, // search across all tags
                cancellationToken);

            if (similar.Count > 0)
            {
                var highestScore = similar[0].Score;
                this.logger.LogWarning(
                    "Duplicate guard rejected ingest: {Count} similar memories found (highest score: {Score:F3}).",
                    similar.Count,
                    highestScore);

                return new IngestResult
                {
                    Success = false,
                    RejectionReason = $"Found {similar.Count} existing memories above similarity threshold ({this.options.DuplicateThreshold:F2}). " +
                        $"Highest similarity: {highestScore:F3}. " +
                        "Use force=true to override, or update the existing memory instead.",
                    SimilarMemories = similar.Select(s => new SimilarMemory
                    {
                        MemoryId = s.MemoryId,
                        Title = s.Title,
                        Content = s.Content,
                        Score = s.Score,
                    }).ToList(),
                };
            }
        }

        var memoryId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var resolvedTags = tags ?? [];

        // Chunk the content
        var chunkInfos = this.chunking.Chunk(content);
        this.logger.LogDebug("Memory {MemoryId}: content split into {ChunkCount} chunks.", memoryId, chunkInfos.Count);

        // Embed chunks. Optimization: reuse the embedding from the dedup check for single-chunk content.
        IReadOnlyList<float[]> vectors;
        if (chunkInfos.Count == 1 && contentVector is not null)
        {
            vectors = [contentVector];
        }
        else
        {
            var chunkTexts = chunkInfos.Select(c => c.Text).ToList();
            vectors = await this.embedding.EmbedBatchAsync(chunkTexts, cancellationToken);
        }

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
        await this.store.StoreMemoryAsync(memoryId, content, chunks, vectors.ToList(), cancellationToken);

        this.logger.LogInformation("Ingested memory {MemoryId} with {ChunkCount} chunks.", memoryId, chunks.Count);
        return new IngestResult { Success = true, MemoryId = memoryId };
    }

    public async Task<MemoryResult?> GetAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        return await this.store.GetMemoryAsync(memoryId, cancellationToken);
    }

    public async Task<MemoryResult?> UpdateAsync(string memoryId, string? content = null, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (!await this.store.ExistsAsync(memoryId, cancellationToken))
        {
            return null;
        }

        if (content is not null)
        {
            // Content changed: need to re-chunk and re-embed.
            // Get existing metadata to preserve title/tags if not being updated.
            var existing = await this.store.GetMemoryAsync(memoryId, cancellationToken);
            if (existing is null)
            {
                return null;
            }

            var resolvedTitle = title ?? existing.Title;
            var resolvedTags = tags ?? existing.Tags;
            var now = DateTimeOffset.UtcNow;

            // Delete old chunks and vectors
            await this.store.DeleteMemoryAsync(memoryId, cancellationToken);

            // Re-chunk
            var chunkInfos = this.chunking.Chunk(content);
            this.logger.LogDebug("Memory {MemoryId}: re-chunked into {ChunkCount} chunks.", memoryId, chunkInfos.Count);

            // Re-embed
            var chunkTexts = chunkInfos.Select(c => c.Text).ToList();
            var vectors = await this.embedding.EmbedBatchAsync(chunkTexts, cancellationToken);

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
            await this.store.StoreMemoryAsync(memoryId, content, chunks, vectors.ToList(), cancellationToken);
            this.logger.LogInformation("Updated memory {MemoryId} content with {ChunkCount} chunks.", memoryId, chunks.Count);
        }
        else
        {
            // Metadata-only update (title and/or tags)
            var now = DateTimeOffset.UtcNow;
            await this.store.UpdateMetadataAsync(memoryId, title, tags, now, cancellationToken);
            this.logger.LogInformation("Updated memory {MemoryId} metadata.", memoryId);
        }

        return await this.store.GetMemoryAsync(memoryId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        var deleted = await this.store.DeleteMemoryAsync(memoryId, cancellationToken);
        if (deleted)
        {
            this.logger.LogInformation("Deleted memory {MemoryId}.", memoryId);
        }
        return deleted;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 5, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty.", nameof(query));
        }

        // Embed the query
        var queryVector = await this.embedding.EmbedAsync(query, cancellationToken);

        // Search
        return await this.store.SearchAsync(queryVector, limit, minScore, tags, cancellationToken);
    }
}
