namespace MemoryMcp.Core.Storage;

/// <summary>
/// Filters applied <b>before</b> scoring by every search list (dense and keyword), so a filter never
/// costs recall: only memories that pass are ranked.
/// </summary>
public sealed record MemorySearchFilter
{
    /// <summary>No filter.</summary>
    public static MemorySearchFilter None { get; } = new();

    /// <summary>Only memories with at least one of these tags (case-insensitive); null or empty = any.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Memories never returned (e.g. quarantined ones); null or empty = none.</summary>
    public IReadOnlySet<string>? ExcludeIds { get; init; }

    /// <summary>True when a tag filter is set.</summary>
    public bool HasTags => this.Tags is { Count: > 0 };

    /// <summary>True when ids are excluded.</summary>
    public bool HasExclusions => this.ExcludeIds is { Count: > 0 };
}
