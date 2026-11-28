namespace MemoryMcp.Shared.Launch;

/// <summary>Starts the service through the systemd user unit the install script registers.</summary>
public sealed class SystemdUserLauncher : IServiceLauncher
{
    private const string Systemctl = "systemctl";

    private readonly string unitName;
    private readonly IProcessRunner runner;

    public SystemdUserLauncher(string dataDirectory, IProcessRunner runner)
    {
        this.unitName = LauncherNames.SystemdUnit(PipeNames.Hash(dataDirectory));
        this.runner = runner;
    }

    public string Name => $"systemd user unit {this.unitName}";

    public async Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Systemctl, ["--user", "cat", this.unitName], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Systemctl, ["--user", "start", this.unitName], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}
