namespace MemoryMcp.Core.Models;

/// <summary>
/// Represents a single chunk of a memory, stored in SQLite.
/// A memory may have one or more chunks depending on content length.
/// The composite key is (MemoryId, ChunkIndex).
/// </summary>
public class ChunkRecord
{
    /// <summary>
    /// The parent memory's identifier (GUID as string).
    /// All chunks sharing this ID belong to the same logical memory.
    /// </summary>
    public required string MemoryId { get; set; }

    /// <summary>
    /// Zero-based index of this chunk within the parent memory.
    /// Used for ordering chunks when reconstructing content.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Character offset where this chunk starts in the original content.
    /// </summary>
    public int StartOffset { get; set; }

    /// <summary>
    /// Character length of this chunk in the original content.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Optional short title/label for the memory.
    /// Duplicated across all chunks of the same memory for query simplicity.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Tags for categorization and filtering.
    /// Stored as a JSON array in SQLite. Duplicated across all chunks of the same memory.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// When the memory was first created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the memory was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Represents the result of a text chunking operation.
/// </summary>
public class ChunkInfo
{
    /// <summary>
    /// Zero-based index of this chunk.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// The chunk text content.
    /// </summary>
    public required string Text { get; set; }

    /// <summary>
    /// Character offset where this chunk starts in the original content.
    /// </summary>
    public int StartOffset { get; set; }

    /// <summary>
    /// Character length of this chunk in the original content.
    /// </summary>
    public int Length { get; set; }
}

/// <summary>
/// Represents a memory returned to the caller (reconstituted from chunks + content file).
/// </summary>
public class MemoryResult
{
    public required string MemoryId { get; set; }
    public string? Title { get; set; }
    public required string Content { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Represents a single search result with similarity score.
/// </summary>
public class SearchResult
{
    public required string MemoryId { get; set; }
    public string? Title { get; set; }
    public required string Content { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Cosine similarity score (0.0 to 1.0, higher = more similar).
    /// </summary>
    public float Score { get; set; }
}
