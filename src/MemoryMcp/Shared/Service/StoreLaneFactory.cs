using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary>Builds each lane on its own private service provider, so no store, connection or embedder is shared.</summary>
public sealed class StoreLaneFactory : IStoreLaneFactory
{
    private readonly IConfiguration configuration;
    private readonly ILoggerFactory loggerFactory;

    public StoreLaneFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        this.configuration = configuration;
        this.loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<StoreLane> CreateAsync(bool runStartup, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddSingleton(this.loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddMemoryMcpCore(this.configuration);

        var provider = services.BuildServiceProvider();
        try
        {
            if (runStartup)
            {
                await StoreStartup.RunAsync(provider, ct).ConfigureAwait(false);
            }
            else
            {
                await provider.GetRequiredService<IMemoryStore>().InitializeAsync().ConfigureAwait(false);
            }

            return new StoreLane(
                provider.GetRequiredService<IMemoryService>(),
                provider.GetRequiredService<IHybridMemorySearch>(),
                provider);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
