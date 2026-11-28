namespace MemoryMcp.Tests.Hosting;

/// <summary>A wall clock in UTC that moves only when the test says so.</summary>
internal sealed class SettableClock : TimeProvider
{
    private DateTimeOffset now;

    public SettableClock(DateTimeOffset start)
    {
        this.now = start;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        return this.now;
    }

    public void Advance(TimeSpan amount)
    {
        this.now += amount;
    }
}
