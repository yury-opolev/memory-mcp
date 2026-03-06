using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Extension methods for registering MemoryMcp.Core services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all MemoryMcp.Core services: chunking, embedding, storage, and memory service.
    /// Configuration is bound from the "MemoryMcp" section.
    /// </summary>
    public static IServiceCollection AddMemoryMcpCore(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<MemoryMcpOptions>(configuration.GetSection(MemoryMcpOptions.SectionName));

        services.AddSingleton<IChunkingService, WordChunkingService>();
        services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
        services.AddSingleton<IMemoryStore, SqliteVecMemoryStore>();
        services.AddSingleton<IMemoryService, MemoryService>();

        return services;
    }
}
