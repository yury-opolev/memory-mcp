using System.Threading.Channels;
using MemoryMcp.Shared.Relay;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A connector that hands out the results the test queues, in order, waiting while none is queued.</summary>
internal sealed class FakeServiceConnector : IServiceConnector
{
    private readonly Channel<ConnectResult> results = Channel.CreateUnbounded<ConnectResult>();
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
        this.results.Writer.TryWrite(new ConnectResult(service.RelayStream, ConnectFailure.None, string.Empty));
        return service;
    }

    public void EnqueueFailure(ConnectFailure failure, string message)
    {
        this.results.Writer.TryWrite(new ConnectResult(null, failure, message));
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

        return await this.results.Reader.ReadAsync(ct);
    }
}
