namespace MemoryMcp.Core.Services;

/// <summary>
/// Generates embedding vectors from text using an embedding model.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for a single text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector as a float array.</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embedding vectors for multiple texts in a batch.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of embedding vectors, one per input text.</returns>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);

        /// <summary>
        /// Embeds a search query, prefixed with <paramref name="instruction"/> in the Qwen3 format
        /// (<see cref="EmbeddingInstruction.Format"/>). Documents are embedded without an instruction.
        /// </summary>
        /// <param name="query">The query text.</param>
        /// <param name="instruction">Task description, or null for no prefix.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<float[]> EmbedQueryAsync(string query, string? instruction, CancellationToken cancellationToken = default) =>
            this.EmbedAsync(EmbeddingInstruction.Format(query, instruction), cancellationToken);
}
