using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// A pool of independent store lanes. A lane serves one call at a time; further calls wait their turn.
/// Lane 0 runs the startup sequence, the others are created after it in the background. A failed startup is not
/// final: the first call that arrives <see cref="StartupRetryDelay"/> or more after the failure starts a new attempt.
/// </summary>
public sealed class StorePool : IAsyncDisposable
{
    /// <summary>How long a call waits for the service to become ready.</summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A wait for a lane longer than this is logged as a warning.</summary>
    public static readonly TimeSpan SlowWaitThreshold = TimeSpan.FromSeconds(1);

    /// <summary>How long after a failed startup a call may trigger the next attempt; calls before that fail at once.</summary>
    public static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(5);

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly IStoreLaneFactory factory;
    private readonly int laneCount;
    private readonly CallMetrics metrics;
    private readonly ILogger<StorePool> logger;
    private readonly TimeProvider time;
    private readonly Channel<StoreLane> freeLanes = Channel.CreateUnbounded<StoreLane>();
    private readonly TaskCompletionSource disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();

    // Guarded by gate: the running or last startup attempt, the creation of the other lanes, and the outcome.
    private Task? startupAttempt;
    private Task? otherLanesCreation;
    private bool isStarted;
    private long startupFailedTimestamp;
    private string? startupError;
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
    public bool IsReady
    {
        get
        {
            lock (this.gate)
            {
                return this.isStarted;
            }
        }
    }

    /// <summary>The message of the latest startup failure, or null when startup has not failed or later succeeded.</summary>
    public string? StartupError
    {
        get
        {
            lock (this.gate)
            {
                return this.startupError;
            }
        }
    }

    /// <summary>Begins creating the lanes in the background: lane 0 with startup, then the rest.</summary>
    public void Start()
    {
        lock (this.gate)
        {
            if (this.isDisposed || this.startupAttempt is not null)
            {
                return;
            }

            this.startupAttempt = this.StartStartupAttempt();
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
        List<Task> creations = [];
        lock (this.gate)
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;
            foreach (var creation in new[] { this.startupAttempt, this.otherLanesCreation })
            {
                if (creation is not null)
                {
                    creations.Add(creation);
                }
            }
        }

        this.freeLanes.Writer.TryComplete();
        await this.shutdown.CancelAsync().ConfigureAwait(false);
        this.disposing.TrySetResult();
        foreach (var creation in creations)
        {
            await creation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    private static ServiceNotReadyException StartupFailed(string? error)
    {
        return new ServiceNotReadyException($"The memory service failed to start: {error}");
    }

    /// <summary>
    /// Returns once startup has succeeded. While an attempt runs, waits for it (bounded by
    /// <see cref="ReadinessTimeout"/>). After a failure, fails at once within <see cref="StartupRetryDelay"/>; later,
    /// starts one new attempt (concurrent callers share it) and waits for that.
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        Task attempt;
        lock (this.gate)
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException(nameof(StorePool));
            }

            if (this.isStarted)
            {
                return;
            }

            if (this.startupAttempt is { IsCompleted: true } && this.startupError is not null)
            {
                if (this.time.GetElapsedTime(this.startupFailedTimestamp) < StartupRetryDelay)
                {
                    throw StartupFailed(this.startupError);
                }

                this.logger.LogInformation("Retrying the memory service startup");
                this.startupAttempt = this.StartStartupAttempt();
            }

            this.startupAttempt ??= this.StartStartupAttempt();
            attempt = this.startupAttempt;
        }

        try
        {
            await Task.WhenAny(attempt, this.disposing.Task).WaitAsync(ReadinessTimeout, this.time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ServiceNotReadyException("The memory service is still starting. Retry the call shortly.");
        }

        lock (this.gate)
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException(nameof(StorePool));
            }

            if (!this.isStarted)
            {
                throw StartupFailed(this.startupError);
            }
        }
    }

    /// <summary>Runs one startup attempt in the background. Called under the gate.</summary>
    private Task StartStartupAttempt()
    {
        return Task.Run(() => this.RunStartupAsync(this.shutdown.Token));
    }

    /// <summary>
    /// Creates lane 0 with the startup sequence. On success it clears the startup error and creates the other lanes in
    /// the background; on a failure it records the error and its time (and logs it once). It never throws.
    /// </summary>
    private async Task RunStartupAsync(CancellationToken ct)
    {
        StoreLane first;
        try
        {
            first = await this.factory.CreateAsync(true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "The memory service failed to start");
            lock (this.gate)
            {
                this.startupError = exception.Message;
                this.startupFailedTimestamp = this.time.GetTimestamp();
            }

            return;
        }

        lock (this.gate)
        {
            this.isStarted = true;
            this.startupError = null;
            if (!this.isDisposed)
            {
                this.otherLanesCreation = Task.Run(() => this.CreateOtherLanesAsync(ct));
            }
        }

        this.ReturnLane(first);
    }

    private async Task CreateOtherLanesAsync(CancellationToken ct)
    {
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
