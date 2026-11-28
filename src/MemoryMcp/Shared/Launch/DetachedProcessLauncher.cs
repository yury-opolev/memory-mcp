using System.Collections;

namespace MemoryMcp.Shared.Launch;

/// <summary>
/// The fallback when no OS launcher is registered: runs <c>serve</c> as a detached process. On Unix it goes through
/// <c>setsid</c> when that is on the PATH, so the service has its own session and outlives the client's. On Windows
/// the process may still be ended with the client's job object; the relay then starts it again. The service gets no
/// <c>MemoryMcp__*</c> variable from the client's environment: it takes its settings from the data directory's
/// settings.json and its own environment, and the data directory travels as an argument.
/// </summary>
public sealed class DetachedProcessLauncher : IServiceLauncher
{
    private const string SettingPrefix = "MemoryMcp__";

    private readonly string dataDirectory;
    private readonly IProcessRunner runner;
    private readonly string? setsidPath;
    private readonly Func<IDictionary> environment;

    public DetachedProcessLauncher(string dataDirectory, IProcessRunner runner)
        : this(dataDirectory, runner, FindSetsid())
    {
    }

    /// <summary>Creates a launcher that wraps the service in <paramref name="setsidPath"/> unless it is null.</summary>
    public DetachedProcessLauncher(string dataDirectory, IProcessRunner runner, string? setsidPath)
        : this(dataDirectory, runner, setsidPath, Environment.GetEnvironmentVariables)
    {
    }

    /// <summary>Creates a launcher that reads this process's environment from <paramref name="environment"/> (a test seam).</summary>
    public DetachedProcessLauncher(string dataDirectory, IProcessRunner runner, string? setsidPath, Func<IDictionary> environment)
    {
        this.dataDirectory = dataDirectory;
        this.runner = runner;
        this.setsidPath = setsidPath;
        this.environment = environment;
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
        var removedVariables = this.ClientSettingNames();
        if (this.setsidPath is not null)
        {
            this.runner.StartDetached(this.setsidPath, [executable, .. serviceArguments], removedVariables);
        }
        else
        {
            this.runner.StartDetached(executable, serviceArguments, removedVariables);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// The names of every <c>MemoryMcp__*</c> variable in this process's environment, matched without regard to case
    /// (configuration keys ignore case, so a differently cased variable would still be read as a setting).
    /// </summary>
    private List<string> ClientSettingNames()
    {
        var names = new List<string>();
        foreach (DictionaryEntry entry in this.environment())
        {
            if (entry.Key is string name && name.StartsWith(SettingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
