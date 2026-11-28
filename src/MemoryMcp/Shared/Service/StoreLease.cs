namespace MemoryMcp.Shared.Service;

/// <summary>Exclusive use of one lane. Disposing returns the lane to the pool and records the run time.</summary>
public sealed class StoreLease : IDisposable
{
    private readonly StorePool pool;
    private readonly TimeSpan wait;
    private readonly long startTimestamp;
    private int isReleased;

    internal StoreLease(StorePool pool, StoreLane lane, TimeSpan wait, long startTimestamp)
    {
        this.pool = pool;
        this.Lane = lane;
        this.wait = wait;
        this.startTimestamp = startTimestamp;
    }

    public StoreLane Lane { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.isReleased, 1) == 0)
        {
            this.pool.Release(this.Lane, this.wait, this.startTimestamp);
        }
    }
}
