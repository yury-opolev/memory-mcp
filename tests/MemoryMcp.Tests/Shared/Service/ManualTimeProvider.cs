namespace MemoryMcp.Tests.Shared.Service;

/// <summary>A time provider whose clock moves only when the test says so. Timers still use the system clock.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        return Interlocked.Read(ref this.timestamp);
    }

    public void Advance(TimeSpan amount)
    {
        Interlocked.Add(ref this.timestamp, amount.Ticks);
    }
}
