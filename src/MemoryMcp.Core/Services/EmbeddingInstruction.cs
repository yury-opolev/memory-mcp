namespace MemoryMcp.Core.Services;

/// <summary>
/// Query-side instruction prefix for instruction-aware embedding models such as Qwen3-Embedding:
/// <c>Instruct: {task}\nQuery:{query}</c>. Documents are embedded without an instruction.
/// </summary>
public static class EmbeddingInstruction
{
    /// <summary>
    /// Returns <paramref name="query"/> prefixed with <paramref name="instruction"/>, or the query
    /// unchanged when the instruction is null or blank.
    /// </summary>
    public static string Format(string query, string? instruction)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.IsNullOrWhiteSpace(instruction)
            ? query
            : "Instruct: " + instruction.Trim() + "\nQuery:" + query;
    }
}
