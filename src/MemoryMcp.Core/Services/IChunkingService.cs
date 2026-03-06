using MemoryMcp.Core.Models;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Splits text content into overlapping chunks for embedding.
/// </summary>
public interface IChunkingService
{
    /// <summary>
    /// Splits the given text into chunks based on configured size and overlap.
    /// </summary>
    /// <param name="text">The text to chunk.</param>
    /// <returns>A list of chunk info objects with text, offset, and length.</returns>
    List<ChunkInfo> Chunk(string text);
}
