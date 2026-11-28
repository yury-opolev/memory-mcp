namespace MemoryMcp.Tests.Shared.Service;

/// <summary>Runs a listener loop for one test; disposing cancels it and waits for it to finish.</summary>
internal sealed class RunningListener : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();

    private RunningListener(Func<CancellationToken, Task> run)
    {
        this.Task = run(this.stopping.Token);
    }

    public Task Task { get; }

    public static RunningListener Start(Func<CancellationToken, Task> run)
    {
        return new RunningListener(run);
    }

    public async ValueTask DisposeAsync()
    {
        await this.stopping.CancelAsync();
        try
        {
            await this.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            this.stopping.Dispose();
        }
    }
}
