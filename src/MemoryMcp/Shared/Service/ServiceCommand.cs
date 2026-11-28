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

    /// <summary>
    /// Exits 0 when the service was stopped on purpose or already runs; exits 1 when its pipes cannot be created
    /// (with current-user-only access, there is no weaker fallback); otherwise runs until shutdown.
    /// </summary>
    public static async Task<int> RunAsync(IConfigurationRoot config, string[] args)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(config);
        using var fileLogs = new FileLoggerProvider(SharedPaths.LogDirectory(dataDirectory));
        var logger = fileLogs.CreateLogger(LogCategory);

        if (StopMarker.Exists(dataDirectory))
        {
            const string StoppedMessage = "The memory service was stopped on purpose; run `MemoryMcp start` to start it.";
            logger.LogInformation(StoppedMessage);
            Console.Out.WriteLine(StoppedMessage);
            return 0;
        }

        using var instanceLock = InstanceLock.TryAcquire(dataDirectory);
        if (instanceLock is null)
        {
            logger.LogInformation("The memory service for {DataDirectory} is already running", dataDirectory);
            Console.Out.WriteLine($"The memory service for {dataDirectory} is already running.");
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(config);

        // The service has no console: everything goes to the rolling log file under the data directory.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(fileLogs);

        MemoryServiceHost.AddServices(builder.Services, config, dataDirectory);

        var host = builder.Build();
        try
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "The memory service could not start");
                await Console.Error.WriteLineAsync(
                    $"The memory service could not start (its pipes must be created with current-user-only access): {exception.Message}")
                    .ConfigureAwait(false);
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
