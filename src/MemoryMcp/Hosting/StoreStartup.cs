using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using MemoryMcp.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Hosting;

/// <summary>Prepares the memory store: format conversion, schema initialisation and the embedder health check.</summary>
public static class StoreStartup
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        // An encrypted database written by the SQLCipher engine of earlier releases is converted once to the
        // format of the current engine (SQLite3 Multiple Ciphers); the original stays beside it as a backup.
        var memoryOptions = scope.ServiceProvider.GetRequiredService<IOptions<MemoryMcpOptions>>().Value;
        if (memoryOptions.Encryption.Enabled)
        {
            var key = await scope.ServiceProvider.GetRequiredService<IKeyStore>().GetOrCreateKeyAsync().ConfigureAwait(false);
            SqlCipherDatabaseConverter.ConvertIfNeeded(
                memoryOptions.DatabasePath,
                Convert.ToBase64String(key),
                scope.ServiceProvider.GetRequiredService<ILogger<Program>>());
        }

        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        await store.InitializeAsync().ConfigureAwait(false);

        // Startup health check: verify Ollama connectivity and model availability
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        if (embeddingService is OllamaEmbeddingService ollamaService)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var isRunning = await ollamaService.IsAvailableAsync().ConfigureAwait(false);
            if (!isRunning)
            {
                logger.LogWarning(
                    "Ollama is not reachable. Embedding-dependent tools (ingest_memory, update_memory, search_memory) will fail. " +
                    "Install Ollama: https://ollama.com/download");
            }
            else
            {
                var modelAvailable = await ollamaService.IsModelAvailableAsync().ConfigureAwait(false);
                if (!modelAvailable)
                {
                    logger.LogWarning(
                        "Ollama is running but the configured embedding model may not be pulled. " +
                        "If embedding fails, run: ollama pull <model-name>");
                }
                else
                {
                    logger.LogInformation("Ollama health check passed: server reachable, model available.");
                }
            }
        }
    }
}
