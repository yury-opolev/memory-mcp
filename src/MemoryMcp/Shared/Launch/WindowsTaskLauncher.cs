namespace MemoryMcp.Shared.Launch;

/// <summary>
/// Starts the service through the scheduled task the install script registers, so it runs outside the client's
/// process tree (and outside any job object the client runs in).
/// </summary>
public sealed class WindowsTaskLauncher : IServiceLauncher
{
    private const string Schtasks = "schtasks";

    private readonly string taskName;
    private readonly IProcessRunner runner;

    public WindowsTaskLauncher(string dataDirectory, IProcessRunner runner)
    {
        this.taskName = LauncherNames.WindowsTask(PipeNames.Hash(dataDirectory));
        this.runner = runner;
    }

    public string Name => $"scheduled task {this.taskName}";

    public async Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Schtasks, ["/Query", "/TN", this.taskName], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Schtasks, ["/Run", "/TN", this.taskName], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}
