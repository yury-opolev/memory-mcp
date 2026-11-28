using System.Threading.Channels;
using MemoryMcp.Shared.Relay;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A connector that hands out the results the test queues, in order, waiting while none is queued.</summary>
internal sealed class FakeServiceConnector : IServiceConnector
{
    private readonly Channel<Func<ConnectResult>> results = Channel.CreateUnbounded<Func<ConnectResult>>();
    private readonly List<TimeSpan> timeouts = [];
    private readonly List<(int Count, TaskCompletionSource Done)> waiters = [];

    public int CallCount
    {
        get
        {
            lock (this.timeouts)
            {
                return this.timeouts.Count;
            }
        }
    }

    public IReadOnlyList<TimeSpan> Timeouts
    {
        get
        {
            lock (this.timeouts)
            {
                return this.timeouts.ToList();
            }
        }
    }

    public FakeServiceEnd EnqueueConnection()
    {
        var service = new FakeServiceEnd();
        return this.EnqueueStream(service.RelayStream, service);
    }

    /// <summary>Queues a connection over <paramref name="stream"/>; returns <paramref name="tag"/> for the test.</summary>
    public T EnqueueStream<T>(Stream stream, T tag)
    {
        this.results.Writer.TryWrite(() => new ConnectResult(stream, ConnectFailure.None, string.Empty));
        return tag;
    }

    public void EnqueueFailure(ConnectFailure failure, string message)
    {
        this.results.Writer.TryWrite(() => new ConnectResult(null, failure, message));
    }

    /// <summary>Makes the next attempt throw <paramref name="exception"/> instead of returning a result.</summary>
    public void EnqueueException(Exception exception)
    {
        this.results.Writer.TryWrite(() => throw exception);
    }

    /// <summary>Completes once <see cref="ConnectAsync"/> was called at least <paramref name="count"/> times.</summary>
    public Task WaitForCallsAsync(int count, CancellationToken ct)
    {
        lock (this.timeouts)
        {
            if (this.timeouts.Count >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiters.Add((count, done));
            return done.Task.WaitAsync(ct);
        }
    }

    public async Task<ConnectResult> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        lock (this.timeouts)
        {
            this.timeouts.Add(timeout);
            foreach (var waiter in this.waiters.Where(waiter => this.timeouts.Count >= waiter.Count).ToList())
            {
                this.waiters.Remove(waiter);
                waiter.Done.TrySetResult();
            }
        }

        var next = await this.results.Reader.ReadAsync(ct);
        return next();
    }
}
