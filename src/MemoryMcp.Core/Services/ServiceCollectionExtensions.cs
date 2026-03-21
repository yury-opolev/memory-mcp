using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Extension methods for registering MemoryMcp.Core services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all MemoryMcp.Core services: chunking, embedding, storage, encryption, and memory service.
    /// Configuration is bound from the "MemoryMcp" section.
    /// </summary>
    public static IServiceCollection AddMemoryMcpCore(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // Initialize SQLCipher provider before any SQLite usage
        SQLitePCL.Batteries_V2.Init();

        services.Configure<MemoryMcpOptions>(configuration.GetSection(MemoryMcpOptions.SectionName));

        services.AddSingleton<IChunkingService, WordChunkingService>();
        services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
        services.AddSingleton<IMemoryService, MemoryService>();

        // Register key store (singleton so the key is retrieved only once)
        services.AddSingleton<IKeyStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryMcpOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return KeyStoreFactory.Create(options.DataDirectory, loggerFactory);
        });

        // Register encryption services
        services.AddSingleton<IContentEncryptor>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryMcpOptions>>().Value;
            if (!options.Encryption.Enabled)
            {
                return new NullContentEncryptor();
            }

            var keyStore = sp.GetRequiredService<IKeyStore>();
            var key = keyStore.GetOrCreateKeyAsync().GetAwaiter().GetResult();
            return new AesGcmContentEncryptor(key);
        });

        // Register memory store (uses same key for SQLCipher)
        services.AddSingleton<IMemoryStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryMcpOptions>>();
            var contentEncryptor = sp.GetRequiredService<IContentEncryptor>();
            var logger = sp.GetRequiredService<ILogger<SqliteVecMemoryStore>>();

            string? databaseKey = null;
            if (options.Value.Encryption.Enabled)
            {
                var keyStore = sp.GetRequiredService<IKeyStore>();
                var key = keyStore.GetOrCreateKeyAsync().GetAwaiter().GetResult();
                databaseKey = Convert.ToBase64String(key);
            }

            return new SqliteVecMemoryStore(options, contentEncryptor, logger, databaseKey);
        });

        return services;
    }
}
