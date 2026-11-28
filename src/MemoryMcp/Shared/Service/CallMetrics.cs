namespace MemoryMcp.Shared.Service;

/// <summary>Wait and run times of memory calls and the number of calls waiting. Holds numbers only, never call content.</summary>
public sealed class CallMetrics
{
    private const int WindowSize = 1000;

    private readonly object gate = new();
    private readonly double[] recentWaits = new double[WindowSize];
    private readonly double[] recentRuns = new double[WindowSize];
    private long calls;
    private int queueDepth;
    private double waitTotal;
    private double runTotal;
    private double waitMax;
    private double runMax;

    /// <summary>Records one finished call.</summary>
    public void Record(TimeSpan wait, TimeSpan run)
    {
        var waitMs = wait.TotalMilliseconds;
        var runMs = run.TotalMilliseconds;
        lock (this.gate)
        {
            var slot = (int)(this.calls % WindowSize);
            this.recentWaits[slot] = waitMs;
            this.recentRuns[slot] = runMs;
            this.calls++;
            this.waitTotal += waitMs;
            this.runTotal += runMs;
            this.waitMax = Math.Max(this.waitMax, waitMs);
            this.runMax = Math.Max(this.runMax, runMs);
        }
    }

    /// <summary>A call starts waiting for a lane.</summary>
    public void EnterQueue()
    {
        lock (this.gate)
        {
            this.queueDepth++;
        }
    }

    /// <summary>A call stops waiting (it got a lane, or gave up).</summary>
    public void LeaveQueue()
    {
        lock (this.gate)
        {
            this.queueDepth--;
        }
    }

    /// <summary>Reads the current numbers. Average and maximum cover all calls; percentiles the most recent 1,000.</summary>
    public CallMetricsSnapshot Snapshot()
    {
        lock (this.gate)
        {
            if (this.calls == 0)
            {
                return new CallMetricsSnapshot(0, this.queueDepth, 0, 0, 0, 0, 0, 0);
            }

            var count = (int)Math.Min(this.calls, WindowSize);
            return new CallMetricsSnapshot(
                this.calls,
                this.queueDepth,
                this.waitTotal / this.calls,
                Percentile95(this.recentWaits, count),
                this.waitMax,
                this.runTotal / this.calls,
                Percentile95(this.recentRuns, count),
                this.runMax);
        }
    }

    private static double Percentile95(double[] window, int count)
    {
        var sorted = window.AsSpan(0, count).ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(0.95 * count);
        return sorted[rank - 1];
    }
}
