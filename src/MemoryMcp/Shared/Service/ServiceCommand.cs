using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp serve</c>: the one process per data directory that owns the store and serves every relay.</summary>
public static class ServiceCommand
{
    private const string LogCategory = "MemoryMcp.Service";
    private const string SdkLogCategory = "ModelContextProtocol";

    /// <summary>
    /// The service's host: the given configuration only, logging to <paramref name="fileLogs"/> only (the service has
    /// no console), and the pooled memory services, tools and listeners.
    /// </summary>
    public static HostApplicationBuilder CreateHostBuilder(IConfigurationRoot config, string[] args, string dataDirectory, FileLoggerProvider fileLogs)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(config);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(fileLogs);

        // The MCP SDK writes request and response content (queries, memory text) at Debug and Trace. The file log
        // must never hold it, whatever levels settings.json asks for, so the SDK's categories stay at Information.
        // A rule for this provider beats every rule that names no provider, and PostConfigure adds it last.
        builder.Services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(
            new LoggerFilterRule(typeof(FileLoggerProvider).FullName, SdkLogCategory, LogLevel.Information, filter: null)));

        MemoryServiceHost.AddServices(builder.Services, config, dataDirectory);
        return builder;
    }

    /// <summary>
    /// The message for a start that failed: a pipe that could not be created is named as such (it must be
    /// current-user-only, there is no weaker fallback); any other failure is given as it is.
    /// </summary>
    public static string StartFailureMessage(Exception exception, bool isPipeCreationFailure)
    {
        return isPipeCreationFailure
            ? $"The memory service could not start: its pipes could not be created with current-user-only access: {exception.Message}"
            : $"The memory service could not start: {exception.Message}";
    }

    /// <summary>
    /// Exits 0 when the service was stopped on purpose or already runs; exits 1 when the data directory cannot be
    /// written or the service cannot start (its pipes cannot be created with current-user-only access, there is no
    /// weaker fallback); otherwise runs until shutdown.
    /// </summary>
    public static async Task<int> RunAsync(IConfigurationRoot config, string[] args)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(config);
        using var fileLogs = new FileLoggerProvider(SharedPaths.LogDirectory(dataDirectory));
        var logger = fileLogs.CreateLogger(LogCategory);

        if (StopMarker.Exists(dataDirectory))
        {
            var stoppedMessage = $"The memory service was stopped on purpose; run {ServiceCommandLines.Start(dataDirectory)} to start it.";
            logger.LogInformation("The memory service was stopped on purpose; it is not started");
            Console.Out.WriteLine(stoppedMessage);
            return 0;
        }

        InstanceLock? acquiredLock;
        try
        {
            acquiredLock = InstanceLock.TryAcquire(dataDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogCritical(exception, "The memory service cannot write its lock file in {DataDirectory}", dataDirectory);
            await Console.Error.WriteLineAsync(
                $"The memory service cannot write its lock file in the data directory {dataDirectory}: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }

        using var instanceLock = acquiredLock;
        if (instanceLock is null)
        {
            logger.LogInformation("The memory service for {DataDirectory} is already running", dataDirectory);
            Console.Out.WriteLine($"The memory service for {dataDirectory} is already running.");
            return 0;
        }

        var host = CreateHostBuilder(config, args, dataDirectory, fileLogs).Build();
        try
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "The memory service could not start");
                var isPipeCreationFailure = host.Services.GetRequiredService<MemoryServiceHost>().PipeCreationFailure is not null;
                await Console.Error.WriteLineAsync(StartFailureMessage(exception, isPipeCreationFailure)).ConfigureAwait(false);
                return 1;
            }

            await host.WaitForShutdownAsync().ConfigureAwait(false);
            return host.Services.GetRequiredService<MemoryServiceHost>().ListenerFailure is null ? 0 : 1;
        }
        finally
        {
            if (host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
    }
}
