namespace MemoryMcp.Shared.Launch;

/// <summary>
/// The fallback when no OS launcher is registered: runs <c>serve</c> as a detached process. On Unix it goes through
/// <c>setsid</c> when that is on the PATH, so the service has its own session and outlives the client's. On Windows
/// the process may still be ended with the client's job object; the relay then starts it again.
/// </summary>
public sealed class DetachedProcessLauncher : IServiceLauncher
{
    private readonly string dataDirectory;
    private readonly IProcessRunner runner;
    private readonly string? setsidPath;

    public DetachedProcessLauncher(string dataDirectory, IProcessRunner runner)
        : this(dataDirectory, runner, FindSetsid())
    {
    }

    /// <summary>Creates a launcher that wraps the service in <paramref name="setsidPath"/> unless it is null.</summary>
    public DetachedProcessLauncher(string dataDirectory, IProcessRunner runner, string? setsidPath)
    {
        this.dataDirectory = dataDirectory;
        this.runner = runner;
        this.setsidPath = setsidPath;
    }

    public string Name => "detached process";

    /// <summary>The full path of <c>setsid</c> on the PATH, or null on Windows or when it is not there.</summary>
    public static string? FindSetsid()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "setsid");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    public Task<bool> StartAsync(CancellationToken ct)
    {
        var executable = ServiceExecutable.Resolve(this.dataDirectory);
        List<string> serviceArguments = ["serve", "--MemoryMcp:DataDirectory=" + this.dataDirectory];
        if (this.setsidPath is not null)
        {
            this.runner.StartDetached(this.setsidPath, [executable, .. serviceArguments]);
        }
        else
        {
            this.runner.StartDetached(executable, serviceArguments);
        }

        return Task.FromResult(true);
    }
}
