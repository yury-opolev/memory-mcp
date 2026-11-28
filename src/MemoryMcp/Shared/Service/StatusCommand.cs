using System.Globalization;
using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp status</c>: prints what the service reports, or that it is not running.</summary>
public static class StatusCommand
{
    public const int RunningExitCode = 0;
    public const int NotRunningExitCode = 3;

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

    /// <summary>Exits 0 and prints the <see cref="ControlResponse"/> fields as lines when running; else exits 3.</summary>
    public static async Task<int> RunAsync(string dataDirectory, string controlPipeName, TextWriter output, CancellationToken ct)
    {
        var response = await ControlClient.SendAsync(controlPipeName, ControlListener.StatusCommand, ct).ConfigureAwait(false);
        if (response is null)
        {
            await output.WriteLineAsync("not running").ConfigureAwait(false);
            await output.WriteLineAsync(StopMarker.Exists(dataDirectory) ? "stop marker: set" : "stop marker: not set").ConfigureAwait(false);
            return NotRunningExitCode;
        }

        var metrics = response.Metrics;
        List<string> lines =
        [
            "running",
            Line("Version", response.Version),
            Line("ProcessId", response.ProcessId),
            Line("Sessions", response.Sessions),
            Line("Ready", response.Ready),
        ];
        if (response.StartupError is not null)
        {
            lines.Add(Line("StartupError", response.StartupError));
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

        return RunningExitCode;
    }

    private static string Line(string name, object value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}: {value}");
    }
}
