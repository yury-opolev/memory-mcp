namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>
/// A time provider whose clock and timers move only when the test advances it. Tests wait for a timer to be scheduled
/// before advancing, so nothing depends on how fast the code under test runs.
/// </summary>
internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> scheduledTimers = [];
    private readonly List<(int Count, TaskCompletionSource Done)> waiters = [];
    private DateTimeOffset now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ScheduledTimerCount
    {
        get
        {
            lock (this.gate)
            {
                return this.scheduledTimers.Count;
            }
        }
    }

    /// <summary>How far from now each scheduled timer is due, shortest first.</summary>
    public IReadOnlyList<TimeSpan> ScheduledDelays
    {
        get
        {
            lock (this.gate)
            {
                return this.scheduledTimers.Select(timer => timer.DueAt!.Value - this.now).Order().ToList();
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (this.gate)
        {
            return this.now;
        }
    }

    public override long GetTimestamp()
    {
        return this.GetUtcNow().UtcTicks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        this.Schedule(timer, dueTime, period);
        return timer;
    }

    /// <summary>Completes when at least <paramref name="count"/> timers are scheduled.</summary>
    public Task WaitForScheduledTimersAsync(int count, CancellationToken ct)
    {
        lock (this.gate)
        {
            if (this.scheduledTimers.Count >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiters.Add((count, done));
            return done.Task.WaitAsync(ct);
        }
    }

    /// <summary>Moves the clock and fires, in due order, every timer that falls due on the way.</summary>
    public void Advance(TimeSpan amount)
    {
        DateTimeOffset target;
        lock (this.gate)
        {
            target = this.now + amount;
        }

        while (true)
        {
            ManualTimer? due;
            lock (this.gate)
            {
                due = this.scheduledTimers.Where(timer => timer.DueAt <= target).MinBy(timer => timer.DueAt);
                if (due is null)
                {
                    this.now = target;
                    return;
                }

                this.now = due.DueAt!.Value;
                if (due.Period == Timeout.InfiniteTimeSpan)
                {
                    due.DueAt = null;
                    this.scheduledTimers.Remove(due);
                }
                else
                {
                    due.DueAt = this.now + due.Period;
                }
            }

            due.Fire();
        }
    }

    internal bool Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (this.gate)
        {
            this.scheduledTimers.Remove(timer);
            timer.Period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = null;
                return true;
            }

            timer.DueAt = this.now + dueTime;
            this.scheduledTimers.Add(timer);
            foreach (var waiter in this.waiters.Where(waiter => this.scheduledTimers.Count >= waiter.Count).ToList())
            {
                this.waiters.Remove(waiter);
                waiter.Done.TrySetResult();
            }

            return true;
        }
    }
}
