namespace MemoryMcp.Shared.Service;

/// <summary>A reading of <see cref="CallMetrics"/>. Times are in milliseconds.</summary>
/// <param name="Calls">Calls recorded since the start.</param>
/// <param name="QueueDepth">Calls currently waiting for a lane.</param>
/// <param name="WaitAverageMs">Average time spent waiting for a lane.</param>
/// <param name="WaitP95Ms">95th percentile wait over the most recent 1,000 calls.</param>
/// <param name="WaitMaxMs">Longest wait.</param>
/// <param name="RunAverageMs">Average time a call held a lane.</param>
/// <param name="RunP95Ms">95th percentile run time over the most recent 1,000 calls.</param>
/// <param name="RunMaxMs">Longest run time.</param>
public sealed record CallMetricsSnapshot(
    long Calls,
    int QueueDepth,
    double WaitAverageMs,
    double WaitP95Ms,
    double WaitMaxMs,
    double RunAverageMs,
    double RunP95Ms,
    double RunMaxMs);
