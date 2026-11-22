namespace MemoryMcp.Core.Models;

/// <summary>One memory found by the keyword (BM25) index.</summary>
/// <param name="MemoryId">The memory.</param>
/// <param name="Score">BM25 relevance (the negated FTS5 <c>bm25()</c> value); higher is better.</param>
public sealed record KeywordHit(string MemoryId, double Score);
