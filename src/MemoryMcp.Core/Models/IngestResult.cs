namespace MemoryMcp.Core.Models;

/// <summary>
/// Result of a memory ingest operation. When near-duplicates are detected
/// and <c>force</c> was not set, the ingest is rejected with details about
/// all conflicting memories so the caller can decide what to do.
/// </summary>
public class IngestResult
{
    /// <summary>Whether the memory was successfully stored.</summary>
    public bool Success { get; init; }

    /// <summary>ID of the newly created memory (when Success=true).</summary>
    public string? MemoryId { get; init; }

    /// <summary>Human-readable reason the ingest was rejected.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>
    /// All existing memories above the duplicate threshold, ordered by
    /// descending similarity. Empty when Success=true or force=true.
    /// </summary>
    public List<SimilarMemory> SimilarMemories { get; init; } = [];
}
