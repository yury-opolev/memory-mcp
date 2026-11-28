using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Shared;

namespace MemoryMcp.Tests.Shared.EndToEnd;

/// <summary>
/// A fresh temp data directory for one test, with its settings.json, the server processes started for it and the
/// commands run against it. Disposing it stops everything the test caused to start, even after a failure: the relays,
/// then the service (<c>stop</c>, then a kill by the process id <c>status</c> reports), then any process of the test
/// executable started since the directory was made, and deletes the directory.
/// </summary>
internal sealed class EndToEndDataDirectory : IAsyncDisposable
{
    /// <summary>The bound of every wait for a condition.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan commandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<ServerProcess> servers = [];
    private readonly DateTime createdAt;

    private EndToEndDataDirectory(string directoryPath)
    {
        this.DirectoryPath = directoryPath;
        this.createdAt = DateTime.Now;
    }

    public string DirectoryPath { get; }

    public string SettingsPath => Path.Combine(this.DirectoryPath, "settings.json");

    public string ServiceLockPath => SharedPaths.ServiceLock(this.DirectoryPath);

    public string StopMarkerPath => SharedPaths.StopMarker(this.DirectoryPath);

    public static EndToEndDataDirectory Create(bool isSharedModeEnabled)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "memory-mcp-e2e-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directoryPath);
        var directory = new EndToEndDataDirectory(directoryPath);
        directory.WriteSettings(isSharedModeEnabled);
        return directory;
    }

    public static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes settings.json: shared mode on or off, encryption off and an embedder endpoint nothing listens on. The
    /// detached service sees no <c>MemoryMcp__*</c> variable, so this file is how it is configured.
    /// </summary>
    public void WriteSettings(bool isSharedModeEnabled)
    {
        var settings = new JsonObject
        {
            ["MemoryMcp"] = new JsonObject
            {
                ["Shared"] = new JsonObject { ["Enabled"] = isSharedModeEnabled },
                ["Encryption"] = new JsonObject { ["Enabled"] = false },
                ["Ollama"] = new JsonObject { ["Endpoint"] = "http://127.0.0.1:9" },
            },
        };
        File.WriteAllText(this.SettingsPath, settings.ToJsonString());
    }

    /// <summary>Starts the executable with no arguments for this directory; it is stopped when the directory is disposed.</summary>
    public ServerProcess StartServer()
    {
        var server = ServerProcess.Start(this.DirectoryPath);
        this.servers.Add(server);
        return server;
    }

    /// <summary>Runs <c>MemoryMcp &lt;arguments&gt;</c> for this directory to its end.</summary>
    public async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = ServerProcess.CreateStartInfo(this.DirectoryPath, arguments);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{ServerProcess.ExecutablePath} did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"MemoryMcp {string.Join(' ', arguments)} did not exit within {commandTimeout.TotalSeconds} seconds.");
        }

        return new CommandResult(process.ExitCode, await output + await error);
    }

    public Task<CommandResult> StatusAsync(CancellationToken ct)
    {
        return this.RunAsync(["status"], ct);
    }

    /// <summary>The service's process id as <c>status</c> reports it, or null when no service answers.</summary>
    public async Task<int?> GetServiceProcessIdAsync(CancellationToken ct)
    {
        var status = await this.StatusAsync(ct);
        if (status.ExitCode != 0)
        {
            return null;
        }

        return int.Parse(status.Field("ProcessId") ?? throw new InvalidOperationException($"status printed no ProcessId:\n{status.Output}"), CultureInfo.InvariantCulture);
    }

    /// <summary>Runs <c>status</c> until <paramref name="condition"/> holds for its result, and returns that result.</summary>
    public async Task<CommandResult> WaitForStatusAsync(Func<CommandResult, bool> condition, string description, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            var status = await this.StatusAsync(ct);
            if (condition(status))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {WaitTimeout.TotalSeconds} seconds for {description}; the last status was:\n{status.Output}");
            }

            await Task.Delay(pollInterval, ct);
        }
    }

    /// <summary>Kills the service process that <c>status</c> reports and waits for it to exit; returns its process id.</summary>
    public async Task<int> KillServiceAsync(CancellationToken ct)
    {
        var processId = await this.GetServiceProcessIdAsync(ct) ?? throw new InvalidOperationException("No memory service is running to kill.");
        await KillAsync(processId);
        return processId;
    }

    /// <summary>
    /// The processes of the test executable started since this directory was made: this test's relays, commands and
    /// services. The tests run one at a time, so no other test's processes are among them.
    /// </summary>
    public List<Process> FindStartedProcesses()
    {
        var started = new List<Process>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServerProcess.ExecutablePath)))
        {
            var isStartedByThisTest = false;
            try
            {
                isStartedByThisTest = !process.HasExited
                    && string.Equals(process.MainModule?.FileName, ServerProcess.ExecutablePath, comparison)
                    && process.StartTime >= this.createdAt;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // It exited while being looked at, or belongs to someone else.
            }

            if (isStartedByThisTest)
            {
                started.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return started;
    }

    /// <summary>Waits until exactly <paramref name="count"/> processes of the test executable started by this test run.</summary>
    public async Task WaitForStartedProcessCountAsync(int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            var started = this.FindStartedProcesses();
            var startedCount = started.Count;
            foreach (var process in started)
            {
                process.Dispose();
            }

            if (startedCount == count)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {WaitTimeout.TotalSeconds} seconds for {count} MemoryMcp processes; {startedCount} run.");
            }

            await Task.Delay(pollInterval, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in this.servers)
        {
            await server.DisposeAsync();
        }

        try
        {
            await this.RunAsync(["stop"], CancellationToken.None);
            if (await this.GetServiceProcessIdAsync(CancellationToken.None) is { } processId)
            {
                await KillAsync(processId);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or FormatException)
        {
            // The sweep below still ends whatever runs.
        }

        // A service that was still starting when stop ran, or a second serve that had not exited yet.
        foreach (var process in this.FindStartedProcesses())
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // It exited in the meantime.
                }
            }
        }

        await this.DeleteDirectoryAsync();
    }

    private static async Task KillAsync(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>Deletes the directory, trying again while a file of an exited process is still being released.</summary>
    private async Task DeleteDirectoryAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Directory.Exists(this.DirectoryPath))
        {
            try
            {
                Directory.Delete(this.DirectoryPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return;
                }

                await Task.Delay(pollInterval);
            }
        }
    }
}
