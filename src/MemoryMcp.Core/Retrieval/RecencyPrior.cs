namespace MemoryMcp.Core.Retrieval;

/// <summary>Optional recency prior for fused rankings (ablated; off by default).</summary>
public static class RecencyPrior
{
    /// <summary>
    /// The multiplier for an item that ended at <paramref name="itemEnd"/>:
    /// <c>1 + weight · 2^(−age / halfLife)</c>; exactly 1 when <paramref name="weight"/> is 0.
    /// </summary>
    public static double Multiplier(DateTimeOffset itemEnd, DateTimeOffset now, double weight, double halfLifeDays)
    {
        if (weight <= 0)
        {
            return 1;
        }

        var ageDays = Math.Max(0, (now - itemEnd).TotalDays);
        return 1 + (weight * Math.Pow(0.5, ageDays / halfLifeDays));
    }
}
