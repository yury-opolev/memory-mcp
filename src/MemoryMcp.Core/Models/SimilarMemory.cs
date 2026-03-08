namespace MemoryMcp.Core.Models;

/// <summary>
/// Represents an existing memory that is semantically similar to a
/// candidate being ingested. Returned as part of <see cref="IngestResult"/>
/// when the duplicate guard rejects an ingest.
/// </summary>
public class SimilarMemory
{
    /// <summary>ID of the existing memory.</summary>
    public required string MemoryId { get; init; }

    /// <summary>Title of the existing memory.</summary>
    public string? Title { get; init; }

    /// <summary>Content of the existing memory.</summary>
    public required string Content { get; init; }

    /// <summary>Cosine similarity score (0.0-1.0) against the candidate content.</summary>
    public float Score { get; init; }
}
