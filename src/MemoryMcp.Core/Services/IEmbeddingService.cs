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
}
