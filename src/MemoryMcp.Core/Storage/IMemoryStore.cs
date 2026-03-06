using MemoryMcp.Core.Models;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Persistence layer for memory chunks, vectors, and content files.
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Initializes the database schema and directories. Must be called once at startup.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores chunks and their vectors for a memory, and writes the content file.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="content">The full memory content (written to file).</param>
    /// <param name="chunks">The chunk records to store.</param>
    /// <param name="vectors">The embedding vectors, one per chunk (same order as chunks).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreMemoryAsync(string memoryId, string content, List<ChunkRecord> chunks, List<float[]> vectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a memory by its ID. Returns metadata from the first chunk and content from file.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The memory result, or null if not found.</returns>
    Task<MemoryResult?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates metadata (title, tags) for all chunks of a memory.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="title">New title (null to leave unchanged).</param>
    /// <param name="tags">New tags (null to leave unchanged).</param>
    /// <param name="updatedAt">The update timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateMetadataAsync(string memoryId, string? title, List<string>? tags, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all chunks, vectors, and the content file for a memory.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the memory existed and was deleted, false if not found.</returns>
    Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for the most similar chunks using vector similarity.
    /// Returns results grouped by memory ID with the best score per memory.
    /// </summary>
    /// <param name="queryVector">The query embedding vector.</param>
    /// <param name="limit">Maximum number of unique memories to return.</param>
    /// <param name="minScore">Minimum similarity score threshold (optional).</param>
    /// <param name="tags">If provided, only return memories that have at least one of these tags.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results ordered by descending similarity score.</returns>
    Task<List<SearchResult>> SearchAsync(float[] queryVector, int limit, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a memory with the given ID exists.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the memory exists.</returns>
    Task<bool> ExistsAsync(string memoryId, CancellationToken cancellationToken = default);
}
