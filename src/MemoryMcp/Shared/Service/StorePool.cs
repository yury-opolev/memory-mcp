using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// A pool of independent store lanes. A lane serves one call at a time; further calls wait their turn.
/// Lane 0 runs the startup sequence, the others are created after it in the background.
/// </summary>
public sealed class StorePool : IAsyncDisposable
{
    /// <summary>How long a call waits for the service to become ready.</summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A wait for a lane longer than this is logged as a warning.</summary>
    public static readonly TimeSpan SlowWaitThreshold = TimeSpan.FromSeconds(1);

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly IStoreLaneFactory factory;
    private readonly int laneCount;
    private readonly CallMetrics metrics;
    private readonly ILogger<StorePool> logger;
    private readonly TimeProvider time;
    private readonly Channel<StoreLane> freeLanes = Channel.CreateUnbounded<StoreLane>();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private Task? creationTask;
    private volatile string? startupError;
    private bool isDisposed;

    public StorePool(IStoreLaneFactory factory, int maxParallelCalls, CallMetrics metrics, ILogger<StorePool> logger, TimeProvider time)
    {
        this.factory = factory;
        this.laneCount = Math.Max(1, maxParallelCalls);
        this.metrics = metrics;
        this.logger = logger;
        this.time = time;
    }

    /// <summary>True once lane 0 finished the startup sequence successfully.</summary>
    public bool IsReady => this.ready.Task.IsCompletedSuccessfully && this.startupError is null;

    /// <summary>The message of the startup failure, or null when startup has not failed.</summary>
    public string? StartupError => this.startupError;

    /// <summary>Begins creating the lanes in the background: lane 0 with startup, then the rest.</summary>
    public void Start()
    {
        lock (this.gate)
        {
            if (this.isDisposed || this.creationTask is not null)
            {
                return;
            }

            this.creationTask = Task.Run(() => this.CreateLanesAsync(this.shutdown.Token));
        }
    }

    /// <summary>Waits for a free lane and leases it. Dispose the lease to return the lane.</summary>
    public async Task<StoreLease> LeaseAsync(string toolName, CancellationToken ct)
    {
        var startTimestamp = this.time.GetTimestamp();
        this.metrics.EnterQueue();
        StoreLane lane;
        try
        {
            await this.WaitUntilReadyAsync(ct).ConfigureAwait(false);
            lane = await this.freeLanes.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(StorePool));
        }
        finally
        {
            this.metrics.LeaveQueue();
        }

        var wait = this.time.GetElapsedTime(startTimestamp);
        if (wait > SlowWaitThreshold)
        {
            this.logger.LogWarning("Call {Tool} waited {WaitMs} ms for a store lane", toolName, (long)wait.TotalMilliseconds);
        }

        return new StoreLease(this, lane, wait, this.time.GetTimestamp());
    }

    /// <summary>Runs <paramref name="action"/> on a leased lane; a busy or locked database becomes a <see cref="DatabaseBusyException"/>.</summary>
    public async Task<TResult> RunAsync<TResult>(string toolName, Func<StoreLane, Task<TResult>> action, CancellationToken ct)
    {
        using var lease = await this.LeaseAsync(toolName, ct).ConfigureAwait(false);
        try
        {
            return await action(lease.Lane).ConfigureAwait(false);
        }
        catch (Exception exception) when (FindBusyError(exception) is { } busy)
        {
            throw new DatabaseBusyException(busy);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? creation;
        lock (this.gate)
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;
            creation = this.creationTask;
        }

        this.freeLanes.Writer.TryComplete();
        await this.shutdown.CancelAsync().ConfigureAwait(false);
        this.ready.TrySetCanceled();
        if (creation is not null)
        {
            try
            {
                await creation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        while (this.freeLanes.Reader.TryRead(out var lane))
        {
            await this.DisposeLaneAsync(lane).ConfigureAwait(false);
        }

        this.shutdown.Dispose();
    }

    internal void Release(StoreLane lane, TimeSpan wait, long runStartTimestamp)
    {
        this.metrics.Record(wait, this.time.GetElapsedTime(runStartTimestamp));
        this.ReturnLane(lane);
    }

    /// <summary>
    /// The busy or locked <see cref="SqliteException"/> behind <paramref name="exception"/> (itself or its inner
    /// exception), matched on the primary result code so extended codes such as 517 and 262 count too.
    /// </summary>
    private static SqliteException? FindBusyError(Exception exception)
    {
        foreach (var candidate in new[] { exception, exception.InnerException })
        {
            if (candidate is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) is SqliteBusy or SqliteLocked)
            {
                return sqlite;
            }
        }

        return null;
    }

    private void ReturnLane(StoreLane lane)
    {
        bool disposed;
        lock (this.gate)
        {
            disposed = this.isDisposed;
            if (!disposed)
            {
                this.freeLanes.Writer.TryWrite(lane);
            }
        }

        if (disposed)
        {
            // The pool shut down while the lane was leased or being created.
            _ = this.DisposeLaneAsync(lane);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        if (!this.ready.Task.IsCompleted)
        {
            try
            {
                await this.ready.Task.WaitAsync(ReadinessTimeout, this.time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ServiceNotReadyException("The memory service is still starting. Retry the call shortly.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(StorePool));
            }
        }

        if (this.startupError is { } error)
        {
            throw new ServiceNotReadyException($"The memory service failed to start: {error}");
        }
    }

    private async Task CreateLanesAsync(CancellationToken ct)
    {
        try
        {
            var first = await this.factory.CreateAsync(true, ct).ConfigureAwait(false);
            this.ReturnLane(first);
            this.ready.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            this.ready.TrySetCanceled();
            return;
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "The memory service failed to start");
            this.startupError = exception.Message;
            this.ready.TrySetResult();
            return;
        }

        for (var index = 1; index < this.laneCount; index++)
        {
            try
            {
                var lane = await this.factory.CreateAsync(false, ct).ConfigureAwait(false);
                this.ReturnLane(lane);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, "Store lane {Lane} could not be created; the pool runs with {Count} lanes", index, index);
                return;
            }
        }
    }

    private async Task DisposeLaneAsync(StoreLane lane)
    {
        try
        {
            await lane.Owner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "A store lane did not dispose cleanly");
        }
    }
}
