namespace MemoryMcp.Shared;

public sealed class SharedOptions
{
    public const string SectionName = "MemoryMcp:Shared";

    private int maxParallelCalls = 1;

    public bool Enabled { get; set; }

    /// <summary>The number of tool calls run in parallel; values below 1 are treated as 1.</summary>
    public int MaxParallelCalls
    {
        get => this.maxParallelCalls;
        set => this.maxParallelCalls = Math.Max(1, value);
    }
}
