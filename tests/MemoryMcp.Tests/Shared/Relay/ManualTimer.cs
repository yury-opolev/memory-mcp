namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A timer of <see cref="ManualTimerTimeProvider"/>: it fires only when the test advances the clock.</summary>
internal sealed class ManualTimer : ITimer
{
    private readonly ManualTimerTimeProvider owner;
    private readonly TimerCallback callback;
    private readonly object? state;

    public ManualTimer(ManualTimerTimeProvider owner, TimerCallback callback, object? state)
    {
        this.owner = owner;
        this.callback = callback;
        this.state = state;
    }

    /// <summary>When the timer fires next, or null when it is not scheduled. Guarded by the owner's lock.</summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>The repeat interval. Guarded by the owner's lock.</summary>
    public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        return this.owner.Schedule(this, dueTime, period);
    }

    public void Fire()
    {
        this.callback(this.state);
    }

    public void Dispose()
    {
        this.owner.Schedule(this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }
}
