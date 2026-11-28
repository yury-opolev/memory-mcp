using System.Reflection;
using System.Runtime.ExceptionServices;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// Runs the service: starts the session and control listeners first, then the store pool, so the MCP handshake
/// is answered while the store is still starting. A listener that fails stops the host.
/// </summary>
public sealed class MemoryServiceHost : IHostedService
{
    private readonly SessionListener sessionListener;
    private readonly ControlListener controlListener;
    private readonly StorePool pool;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<MemoryServiceHost> logger;
    private readonly CancellationTokenSource stopping = new();
    private Task? listeners;

    public MemoryServiceHost(
        SessionListener sessionListener,
        ControlListener controlListener,
        StorePool pool,
        IHostApplicationLifetime lifetime,
        ILogger<MemoryServiceHost> logger)
    {
        this.sessionListener = sessionListener;
        this.controlListener = controlListener;
        this.pool = pool;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    /// <summary>The version of this build, as reported by <c>status</c> and written to the lock file.</summary>
    public static string Version { get; } =
        typeof(MemoryServiceHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MemoryServiceHost).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The failure of a listener after start, which ended the service; null otherwise.</summary>
    public Exception? ListenerFailure { get; private set; }

    /// <summary>Why <see cref="StartAsync"/> failed when a listener could not create its pipe; null otherwise.</summary>
    public Exception? PipeCreationFailure { get; private set; }

    /// <summary>
    /// Registers the pooled memory services, the MCP tools (without a transport: sessions are created per
    /// connection), both listeners and this host.
    /// </summary>
    public static void AddServices(IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        var sharedOptions = configuration.GetSection(SharedOptions.SectionName).Get<SharedOptions>() ?? new SharedOptions();

        services.Configure<MemoryMcpOptions>(configuration.GetSection(MemoryMcpOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CallMetrics>();
        services.AddSingleton<IStoreLaneFactory>(provider => new StoreLaneFactory(configuration, provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(provider => new StorePool(
            provider.GetRequiredService<IStoreLaneFactory>(),
            sharedOptions.MaxParallelCalls,
            provider.GetRequiredService<CallMetrics>(),
            provider.GetRequiredService<ILogger<StorePool>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMemoryService, PooledMemoryService>();
        services.AddSingleton<IHybridMemorySearch, PooledHybridMemorySearch>();

        services.AddMcpServer().WithToolsFromAssembly(typeof(MemoryTools).Assembly);

        services.AddSingleton(provider => new SessionListener(
            PipeNames.Session(dataDirectory),
            provider,
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(provider => new ControlListener(
            PipeNames.Control(dataDirectory),
            dataDirectory,
            provider.GetRequiredService<StorePool>(),
            provider.GetRequiredService<CallMetrics>(),
            provider.GetRequiredService<SessionListener>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            provider.GetRequiredService<ILogger<ControlListener>>()));
        services.AddSingleton<MemoryServiceHost>();
        services.AddHostedService(provider => provider.GetRequiredService<MemoryServiceHost>());
    }

    /// <summary>Starts the listeners, then the store. Throws when a listener cannot create its pipe.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sessions = this.sessionListener.RunAsync(this.stopping.Token);
        var control = this.controlListener.RunAsync(this.stopping.Token);
        if (sessions.IsFaulted || control.IsFaulted)
        {
            var failure = (sessions.IsFaulted ? sessions.Exception : control.Exception)!.GetBaseException();
            this.PipeCreationFailure = failure;
            await this.stopping.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(sessions, control).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ExceptionDispatchInfo.Throw(failure);
        }

        this.listeners = this.WatchListenersAsync(sessions, control);
        this.pool.Start();
        this.logger.LogInformation("Memory service {Version} started (process {ProcessId})", Version, Environment.ProcessId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.stopping.CancelAsync().ConfigureAwait(false);
        if (this.listeners is not null)
        {
            try
            {
                await this.listeners.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.logger.LogWarning("Sessions did not end within the shutdown timeout");
            }
        }

        this.logger.LogInformation("Memory service stopped");
    }

    private async Task WatchListenersAsync(Task sessions, Task control)
    {
        var first = await Task.WhenAny(sessions, control).ConfigureAwait(false);
        if (!this.stopping.IsCancellationRequested)
        {
            this.ListenerFailure = first.Exception?.GetBaseException()
                ?? new InvalidOperationException("A listener ended unexpectedly.");
            this.logger.LogCritical(this.ListenerFailure, "A listener failed; the memory service stops");
            this.lifetime.StopApplication();
        }

        await Task.WhenAll(sessions, control).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var listener in new[] { sessions, control })
        {
            if (listener.IsFaulted && listener != first)
            {
                this.logger.LogError(listener.Exception.GetBaseException(), "A listener failed while the service stopped");
            }
        }
    }
}
