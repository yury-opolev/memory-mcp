using System.Globalization;
using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp status</c>: prints what the service reports, or that it is not running.</summary>
public static class StatusCommand
{
    /// <summary>The service runs and its store is ready.</summary>
    public const int RunningExitCode = 0;

    /// <summary>No service is running.</summary>
    public const int NotRunningExitCode = 3;

    /// <summary>A service runs but is not ready: still starting, its startup failed, or it does not answer.</summary>
    public const int NotReadyExitCode = 4;

    public const string ReadyLine = "running";
    public const string StartingLine = "running, still starting";
    public const string StartupFailedLine = "running, startup failed";
    public const string NotAnsweringLine = "running, not answering";
    public const string NotRunningLine = "not running";

    /// <summary>Prints only the launcher hash of the data directory (for the install scripts) and contacts no service.</summary>
    public const string PrintHashOption = "--print-hash";

    public static Task<int> RunAsync(IConfigurationRoot configuration, string[] args)
    {
        if (IsPrintHash(args))
        {
            return PrintHashAsync(PrintHashDataDirectory(args), Console.Out);
        }

        var dataDirectory = ServerConfiguration.DataDirectory(configuration);
        return RunAsync(dataDirectory, PipeNames.Control(dataDirectory), Console.Out, CancellationToken.None);
    }

    public static bool IsPrintHash(string[] args)
    {
        return args.Contains(PrintHashOption, StringComparer.Ordinal);
    }

    /// <summary>
    /// The data directory for <see cref="PrintHashOption"/>. The command line configuration reads a bare
    /// <c>--print-hash</c> as a key whose value is the next argument, which would swallow a data directory given after
    /// it, so the configuration is built again from the arguments without the flag.
    /// </summary>
    public static string PrintHashDataDirectory(string[] args)
    {
        string[] settingArguments = [.. args.Where(argument => !string.Equals(argument, PrintHashOption, StringComparison.Ordinal))];
        return ServerConfiguration.DataDirectory(ServerConfiguration.Build(settingArguments));
    }

    /// <summary>Prints the <see cref="PipeNames.Hash"/> of <paramref name="dataDirectory"/> as the only line and exits 0.</summary>
    public static async Task<int> PrintHashAsync(string dataDirectory, TextWriter output)
    {
        await output.WriteLineAsync(PipeNames.Hash(dataDirectory)).ConfigureAwait(false);
        return 0;
    }

    public static Task<int> RunAsync(string dataDirectory, string controlPipeName, TextWriter output, CancellationToken ct)
    {
        return RunAsync(dataDirectory, controlPipeName, output, ControlClient.AnswerTimeout, ct);
    }

    /// <summary>
    /// Prints the state as the first line, then the <see cref="ControlResponse"/> fields: exits 0 when the service is
    /// ready, 4 when it runs but is still starting, failed to start or does not answer within
    /// <paramref name="answerTimeout"/>, and 3 when it is not running.
    /// </summary>
    public static async Task<int> RunAsync(string dataDirectory, string controlPipeName, TextWriter output, TimeSpan answerTimeout, CancellationToken ct)
    {
        var reply = await ControlClient.ExchangeAsync(controlPipeName, ControlListener.StatusCommand, answerTimeout, ct).ConfigureAwait(false);
        if (reply.Outcome == ControlOutcome.NotRunning)
        {
            await output.WriteLineAsync(NotRunningLine).ConfigureAwait(false);
            await output.WriteLineAsync(StopMarker.Exists(dataDirectory) ? "stop marker: set" : "stop marker: not set").ConfigureAwait(false);
            return NotRunningExitCode;
        }

        if (reply.Response is not { } response)
        {
            await output.WriteLineAsync(NotAnsweringLine).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"The service accepted the connection but did not answer within {(int)answerTimeout.TotalSeconds} seconds. See the service log in {SharedPaths.LogDirectory(dataDirectory)}.")
                .ConfigureAwait(false);
            return NotReadyExitCode;
        }

        var stateLine = response switch
        {
            { Ready: true } => ReadyLine,
            { StartupError: not null } => StartupFailedLine,
            _ => StartingLine,
        };
        var metrics = response.Metrics;
        List<string> lines =
        [
            stateLine,
            Line("Version", response.Version),
            Line("ProcessId", response.ProcessId),
            Line("Sessions", response.Sessions),
            Line("Ready", response.Ready),
        ];
        if (response.StartupError is not null)
        {
            lines.Add(Line("StartupError", response.StartupError));
            lines.Add(Line("LogDirectory", SharedPaths.LogDirectory(dataDirectory)));
        }

        lines.AddRange(
        [
            Line("Calls", metrics.Calls),
            Line("QueueDepth", metrics.QueueDepth),
            Line("WaitAverageMs", metrics.WaitAverageMs),
            Line("WaitP95Ms", metrics.WaitP95Ms),
            Line("WaitMaxMs", metrics.WaitMaxMs),
            Line("RunAverageMs", metrics.RunAverageMs),
            Line("RunP95Ms", metrics.RunP95Ms),
            Line("RunMaxMs", metrics.RunMaxMs),
        ]);
        foreach (var line in lines)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }

        return response.Ready ? RunningExitCode : NotReadyExitCode;
    }

    private static string Line(string name, object value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}: {value}");
    }
}
