using System.Globalization;
using System.Runtime.InteropServices;

namespace MemoryMcp.Shared.Launch;

/// <summary>Starts the service through the launchd agent the install script registers in the user's GUI domain.</summary>
public sealed class LaunchdLauncher : IServiceLauncher
{
    private const string Launchctl = "launchctl";

    private readonly string label;
    private readonly IProcessRunner runner;
    private readonly Func<uint> userId;

    public LaunchdLauncher(string dataDirectory, IProcessRunner runner)
        : this(dataDirectory, runner, GetUserId)
    {
    }

    /// <summary>Creates a launcher that asks <paramref name="userId"/> for the user id when it runs a command (a test seam).</summary>
    public LaunchdLauncher(string dataDirectory, IProcessRunner runner, Func<uint> userId)
    {
        this.label = LauncherNames.LaunchdLabel(PipeNames.Hash(dataDirectory));
        this.runner = runner;
        this.userId = userId;
    }

    public string Name => $"launchd agent {this.label}";

    private string Target => string.Create(CultureInfo.InvariantCulture, $"gui/{this.userId()}/{this.label}");

    public async Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Launchctl, ["print", this.Target], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        var result = await this.runner.RunAsync(Launchctl, ["kickstart", this.Target], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
