using MemoryMcp.Hosting;
using MemoryMcp.Shared.Launch;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp start</c>: clears the stop marker and starts the service through its launcher.</summary>
public static class StartCommand
{
    /// <summary>How long the started service may take to answer <c>status</c>.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(250);

    public static Task<int> RunAsync(IConfigurationRoot configuration)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(configuration);
        var launchers = ServiceLauncherFactory.Create(dataDirectory, new ProcessRunner());
        return RunAsync(dataDirectory, PipeNames.Control(dataDirectory), launchers, Console.Out, Console.Error, StartTimeout, CancellationToken.None);
    }

    /// <summary>
    /// Clears the stop marker; when the service is not running, starts it once with the first launcher that accepts
    /// (one that fails or times out hands over to the next) and waits until it answers <c>status</c>. Exits 0 when it
    /// runs, 1 when it could not be started in time.
    /// </summary>
    public static async Task<int> RunAsync(
        string dataDirectory,
        string controlPipeName,
        IReadOnlyList<IServiceLauncher> launchers,
        TextWriter output,
        TextWriter error,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (StopMarker.Exists(dataDirectory))
        {
            StopMarker.Clear(dataDirectory);
        }

        var running = await ControlClient.SendAsync(controlPipeName, ControlListener.StatusCommand, ct).ConfigureAwait(false);
        if (running is not null)
        {
            await output.WriteLineAsync($"The memory service is already running (process {running.ProcessId}).").ConfigureAwait(false);
            return 0;
        }

        var started = DateTime.UtcNow;
        var failures = new List<string>();
        var launcher = await ServiceLauncherFactory.StartFirstAvailableAsync(
            launchers,
            ServiceLauncherFactory.LauncherCallTimeout,
            failures.Add,
            ct).ConfigureAwait(false);
        if (launcher is null)
        {
            foreach (var failure in failures)
            {
                await error.WriteLineAsync(failure).ConfigureAwait(false);
            }

            await error.WriteLineAsync("The memory service could not be started: no launcher accepted the request.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"Starting the memory service through the {launcher.Name}.").ConfigureAwait(false);
        while (DateTime.UtcNow - started < timeout)
        {
            var response = await ControlClient.SendAsync(controlPipeName, ControlListener.StatusCommand, ct).ConfigureAwait(false);
            if (response is not null)
            {
                await output.WriteLineAsync($"The memory service is running (process {response.ProcessId}, version {response.Version}).").ConfigureAwait(false);
                return 0;
            }

            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        await error.WriteLineAsync(
            $"The memory service did not answer within {(int)timeout.TotalSeconds} seconds. See the service log in {SharedPaths.LogDirectory(dataDirectory)}.")
            .ConfigureAwait(false);
        return 1;
    }
}
