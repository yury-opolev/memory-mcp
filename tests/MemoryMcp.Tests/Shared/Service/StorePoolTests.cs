using MemoryMcp.Core.Services;
using MemoryMcp.Shared.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public class StorePoolTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StoreLane NewLane()
    {
        return new StoreLane(Substitute.For<IMemoryService>(), Substitute.For<IHybridMemorySearch>(), Substitute.For<IAsyncDisposable>());
    }

    private static IStoreLaneFactory NewFactory()
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(NewLane()));
        return factory;
    }

    private static StorePool NewPool(IStoreLaneFactory factory, int lanes, CallMetrics? metrics = null, ListLogger<StorePool>? logger = null, TimeProvider? time = null)
    {
        var pool = new StorePool(factory, lanes, metrics ?? new CallMetrics(), logger ?? new ListLogger<StorePool>(), time ?? TimeProvider.System);
        pool.Start();
        return pool;
    }

    [Fact]
    public async Task LeaseAsync_OneLane_SecondCallWaitsUntilFirstReleases()
    {
        await using var pool = NewPool(NewFactory(), 1);
        var first = await pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token);

        var second = pool.LeaseAsync("second", Token);

        Assert.False(second.IsCompleted);
        first.Dispose();
        using var secondLease = await second.WaitAsync(TestTimeout, Token);
        Assert.Same(first.Lane, secondLease.Lane);
    }

    [Fact]
    public async Task LeaseAsync_TwoLanes_TwoCallsOverlap_ThirdWaits()
    {
        await using var pool = NewPool(NewFactory(), 2);
        var first = await pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token);
        using var second = await pool.LeaseAsync("second", Token).WaitAsync(TestTimeout, Token);

        var third = pool.LeaseAsync("third", Token);

        Assert.NotSame(first.Lane, second.Lane);
        Assert.False(third.IsCompleted);
        first.Dispose();
        using var thirdLease = await third.WaitAsync(TestTimeout, Token);
        Assert.Same(first.Lane, thirdLease.Lane);
    }

    [Fact]
    public async Task LeaseAsync_BeforeStartupFinishes_WaitsThenSucceeds()
    {
        var startup = new TaskCompletionSource<StoreLane>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(startup.Task);
        await using var pool = NewPool(factory, 1);

        var lease = pool.LeaseAsync("early", Token);

        Assert.False(lease.IsCompleted);
        var lane = NewLane();
        startup.SetResult(lane);
        using var acquired = await lease.WaitAsync(TestTimeout, Token);
        Assert.Same(lane, acquired.Lane);
    }

    [Fact]
    public async Task LeaseAsync_StartupFailed_ThrowsServiceNotReadyWithStartupMessage()
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromException<StoreLane>(new InvalidOperationException("the database is corrupt")));
        await using var pool = NewPool(factory, 1);

        var exception = await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("any", Token).WaitAsync(TestTimeout, Token));

        Assert.Contains("the database is corrupt", exception.Message);
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("again", Token).WaitAsync(TestTimeout, Token));
    }

    [Fact]
    public async Task StartupFailed_LeaseAfterTheRetryDelay_RetriesStartup_AndSucceeds()
    {
        var time = new ManualTimeProvider();
        var logger = new ListLogger<StorePool>();
        var factory = Substitute.For<IStoreLaneFactory>();
        var lane = NewLane();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<StoreLane>(new InvalidOperationException("the database is locked")),
            Task.FromResult(lane));
        await using var pool = NewPool(factory, 1, logger: logger, time: time);
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token));
        Assert.Equal("the database is locked", pool.StartupError);

        time.Advance(StorePool.StartupRetryDelay);
        using var lease = await pool.LeaseAsync("after the delay", Token).WaitAsync(TestTimeout, Token);

        Assert.Same(lane, lease.Lane);
        Assert.True(pool.IsReady);
        Assert.Null(pool.StartupError);
        await factory.Received(2).CreateAsync(true, Arg.Any<CancellationToken>());
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task StartupFailed_LeaseInsideTheRetryDelay_FailsWithoutAnAttempt()
    {
        var time = new ManualTimeProvider();
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<StoreLane>(new InvalidOperationException("the key store is not ready")),
            Task.FromResult(NewLane()));
        await using var pool = NewPool(factory, 1, time: time);
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token));

        time.Advance(StorePool.StartupRetryDelay - TimeSpan.FromTicks(1));
        var exception = await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("too early", Token).WaitAsync(TestTimeout, Token));

        Assert.Contains("the key store is not ready", exception.Message);
        await factory.Received(1).CreateAsync(true, Arg.Any<CancellationToken>());
        Assert.False(pool.IsReady);
    }

    [Fact]
    public async Task StartupFailed_TwoLeasesAfterTheDelay_TriggerOneAttempt_AndBothSucceed()
    {
        var time = new ManualTimeProvider();
        var retry = new TaskCompletionSource<StoreLane>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<StoreLane>(new InvalidOperationException("busy")),
            retry.Task);
        await using var pool = NewPool(factory, 1, time: time);
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token));
        time.Advance(StorePool.StartupRetryDelay);

        var firstLease = pool.LeaseAsync("a", Token);
        var secondLease = pool.LeaseAsync("b", Token);
        Assert.False(firstLease.IsCompleted);
        Assert.False(secondLease.IsCompleted);
        retry.SetResult(NewLane());

        var first = await firstLease.WaitAsync(TestTimeout, Token);
        first.Dispose();
        using var second = await secondLease.WaitAsync(TestTimeout, Token);
        await factory.Received(2).CreateAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartupRetryFails_TheWaitingCallGetsTheLatestError_AndItIsLoggedOnce()
    {
        var time = new ManualTimeProvider();
        var logger = new ListLogger<StorePool>();
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<StoreLane>(new InvalidOperationException("first failure")),
            Task.FromException<StoreLane>(new InvalidOperationException("second failure")));
        await using var pool = NewPool(factory, 1, logger: logger, time: time);
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("first", Token).WaitAsync(TestTimeout, Token));
        time.Advance(StorePool.StartupRetryDelay);

        var exception = await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.LeaseAsync("retry", Token).WaitAsync(TestTimeout, Token));

        Assert.Contains("second failure", exception.Message);
        Assert.Equal("second failure", pool.StartupError);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Error));
    }

    [Fact]
    public async Task LeaseAsync_CancelledWhileWaiting_LeavesQueueDepthAtZero()
    {
        var metrics = new CallMetrics();
        await using var pool = NewPool(NewFactory(), 1, metrics);
        var held = await pool.LeaseAsync("held", Token).WaitAsync(TestTimeout, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var waiting = pool.LeaseAsync("waiting", cancellation.Token);
        Assert.Equal(1, metrics.Snapshot().QueueDepth);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TestTimeout, Token));
        Assert.Equal(0, metrics.Snapshot().QueueDepth);

        // The cancelled waiter did not take the lane with it.
        held.Dispose();
        using var next = await pool.LeaseAsync("next", Token).WaitAsync(TestTimeout, Token);
        Assert.Same(held.Lane, next.Lane);
    }

    [Fact]
    public async Task Start_RunsStartupOnFirstLaneOnly()
    {
        var factory = NewFactory();
        await using var pool = NewPool(factory, 3);

        // Leasing all three lanes proves all three were created.
        using var first = await pool.LeaseAsync("a", Token).WaitAsync(TestTimeout, Token);
        using var second = await pool.LeaseAsync("b", Token).WaitAsync(TestTimeout, Token);
        using var third = await pool.LeaseAsync("c", Token).WaitAsync(TestTimeout, Token);

        await factory.Received(1).CreateAsync(true, Arg.Any<CancellationToken>());
        await factory.Received(2).CreateAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaseAsync_WaitOverThreshold_LogsWarningWithToolNameAndNoArguments()
    {
        var time = new ManualTimeProvider();
        var logger = new ListLogger<StorePool>();
        await using var pool = NewPool(NewFactory(), 1, logger: logger, time: time);
        var held = await pool.LeaseAsync("held", Token).WaitAsync(TestTimeout, Token);

        var slow = pool.LeaseAsync("search_memory", Token);
        time.Advance(StorePool.SlowWaitThreshold + TimeSpan.FromSeconds(1));
        held.Dispose();
        using var lease = await slow.WaitAsync(TestTimeout, Token);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("search_memory", warning.Message);
        Assert.Contains("2000", warning.Message);
    }

    [Fact]
    public async Task LeaseAsync_WaitUnderThreshold_LogsNothing()
    {
        var time = new ManualTimeProvider();
        var logger = new ListLogger<StorePool>();
        await using var pool = NewPool(NewFactory(), 1, logger: logger, time: time);
        var held = await pool.LeaseAsync("held", Token).WaitAsync(TestTimeout, Token);

        var quick = pool.LeaseAsync("quick", Token);
        time.Advance(TimeSpan.FromMilliseconds(500));
        held.Dispose();
        using var lease = await quick.WaitAsync(TestTimeout, Token);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task LeaseDispose_RecordsRunTimeOnce()
    {
        var time = new ManualTimeProvider();
        var metrics = new CallMetrics();
        await using var pool = NewPool(NewFactory(), 1, metrics, time: time);

        var lease = await pool.LeaseAsync("timed", Token).WaitAsync(TestTimeout, Token);
        time.Advance(TimeSpan.FromMilliseconds(250));
        lease.Dispose();
        lease.Dispose();

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.Calls);
        Assert.Equal(250, snapshot.RunMaxMs);
        Assert.Equal(0, snapshot.WaitMaxMs);
    }

    [Fact]
    public async Task DisposeAsync_LeasedLane_IsDisposedWhenReturned()
    {
        var pool = NewPool(NewFactory(), 1);
        var lease = await pool.LeaseAsync("held", Token).WaitAsync(TestTimeout, Token);
        var owner = lease.Lane.Owner;

        await pool.DisposeAsync();
        await owner.DidNotReceive().DisposeAsync();
        lease.Dispose();

        await owner.Received(1).DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.LeaseAsync("late", Token));
    }

    [Fact]
    public async Task DisposeAsync_FreeLane_IsDisposed()
    {
        var pool = NewPool(NewFactory(), 1);
        var lease = await pool.LeaseAsync("once", Token).WaitAsync(TestTimeout, Token);
        var owner = lease.Lane.Owner;
        lease.Dispose();

        await pool.DisposeAsync();

        await owner.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhileCallWaiting_FailsTheWaiter()
    {
        var pool = NewPool(NewFactory(), 1);
        var held = await pool.LeaseAsync("held", Token).WaitAsync(TestTimeout, Token);
        var waiting = pool.LeaseAsync("waiting", Token);

        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TestTimeout, Token));
        held.Dispose();
    }
}
