using MemoryMcp.Shared.Service;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public class CallMetricsTests
{
    [Fact]
    public void Snapshot_NoCalls_AllZero()
    {
        var snapshot = new CallMetrics().Snapshot();

        Assert.Equal(new CallMetricsSnapshot(0, 0, 0, 0, 0, 0, 0, 0), snapshot);
    }

    [Fact]
    public void Snapshot_ReportsAverageP95AndMax()
    {
        var metrics = new CallMetrics();
        for (var step = 1; step <= 100; step++)
        {
            metrics.Record(TimeSpan.FromMilliseconds(step * 10), TimeSpan.FromMilliseconds(step));
        }

        var snapshot = metrics.Snapshot();

        Assert.Equal(100, snapshot.Calls);
        Assert.Equal(1000, snapshot.WaitMaxMs);
        Assert.Equal(950, snapshot.WaitP95Ms);
        Assert.Equal(505, snapshot.WaitAverageMs);
        Assert.Equal(100, snapshot.RunMaxMs);
        Assert.Equal(95, snapshot.RunP95Ms);
        Assert.Equal(50.5, snapshot.RunAverageMs);
    }

    [Fact]
    public void Snapshot_MoreThanThousandCalls_PercentileCoversMostRecentThousand()
    {
        var metrics = new CallMetrics();
        for (var call = 0; call < 500; call++)
        {
            metrics.Record(TimeSpan.FromMilliseconds(5000), TimeSpan.Zero);
        }

        for (var call = 0; call < 1000; call++)
        {
            metrics.Record(TimeSpan.FromMilliseconds(1), TimeSpan.Zero);
        }

        var snapshot = metrics.Snapshot();

        Assert.Equal(1500, snapshot.Calls);
        Assert.Equal(1, snapshot.WaitP95Ms);
        Assert.Equal(5000, snapshot.WaitMaxMs);
    }

    [Fact]
    public void Snapshot_QueueDepth_FollowsEnterAndLeave()
    {
        var metrics = new CallMetrics();

        metrics.EnterQueue();
        metrics.EnterQueue();
        Assert.Equal(2, metrics.Snapshot().QueueDepth);

        metrics.LeaveQueue();
        Assert.Equal(1, metrics.Snapshot().QueueDepth);
    }
}
