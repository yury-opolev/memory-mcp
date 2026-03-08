using MemoryMcp.Core.Models;

namespace MemoryMcp.Core.Services;

/// <summary>
/// High-level memory operations: ingest, get, update, delete, search.
/// Orchestrates chunking, embedding, and storage.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Ingests a new memory: chunks the content, embeds each chunk, and stores everything.
    /// When <paramref name="force"/> is false (default), performs a similarity check first
    /// and rejects the ingest if any near-duplicates exist above the configured threshold.
    /// </summary>
    /// <param name="content">The full memory content text.</param>
    /// <param name="title">Optional short title/label.</param>
    /// <param name="tags">Optional tags for categorization.</param>
    /// <param name="force">If true, bypass the duplicate check and always store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="IngestResult"/> indicating success or duplicate rejection
    /// with details of all similar memories found.</returns>
    Task<IngestResult> IngestAsync(string content, string? title = null, List<string>? tags = null, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a memory by its ID, including full content.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The memory result, or null if not found.</returns>
    Task<MemoryResult?> GetAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing memory. If content changes, re-chunks and re-embeds.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="content">New content (null to leave unchanged).</param>
    /// <param name="title">New title (null to leave unchanged).</param>
    /// <param name="tags">New tags (null to leave unchanged).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated memory result.</returns>
    Task<MemoryResult?> UpdateAsync(string memoryId, string? content = null, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a memory and all its chunks.
    /// </summary>
    /// <param name="memoryId">The memory identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the memory was found and deleted.</returns>
    Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches memories by semantic similarity to the query text.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum similarity score threshold (optional).</param>
    /// <param name="tags">Filter by tags: only return memories with at least one matching tag (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results ranked by similarity.</returns>
    Task<List<SearchResult>> SearchAsync(string query, int limit = 5, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default);
}
