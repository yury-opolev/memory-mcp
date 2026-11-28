using MemoryMcp.Hosting;
using MemoryMcp.Shared.Launch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MemoryMcp.Shared.Relay;

/// <summary>
/// The relay role: forwards this process's stdio MCP session to the memory service. It stays thin: it opens no
/// database and loads no embedder; its log goes to stderr only, since stdout is the client's MCP channel.
/// </summary>
public static class RelayCommand
{
    private const string LogCategory = "MemoryMcp.Relay";

    public static async Task<int> RunAsync(IConfigurationRoot configuration, string[] args)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(configuration);
        var settingsPath = Path.Combine(dataDirectory, "settings.json");
        foreach (var name in ClientEnvironmentCheck.IgnoredSettings(Environment.GetEnvironmentVariables()))
        {
            await Console.Error.WriteLineAsync($"{name} is ignored in shared mode; put it in {settingsPath}.").ConfigureAwait(false);
        }

        using var loggers = LoggerFactory.Create(logging => logging
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Disabled;
            })
            .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        var logger = loggers.CreateLogger(LogCategory);

        var connector = new PipeServiceConnector(
            args,
            dataDirectory,
            PipeNames.Session(dataDirectory),
            ServiceLauncherFactory.Create(dataDirectory, new ProcessRunner()),
            logger);
        await using var clientInput = Console.OpenStandardInput();
        await using var clientOutput = Console.OpenStandardOutput();
        var session = new RelaySession(clientInput, clientOutput, connector, logger, TimeProvider.System);
        return await session.RunAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
