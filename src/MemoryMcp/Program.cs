using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Load user settings from data directory (survives rebuilds)
var dataDir = builder.Configuration[$"{MemoryMcpOptions.SectionName}:DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "memory-mcp");
var userSettings = Path.Combine(dataDir, "settings.json");
if (File.Exists(userSettings))
{
    builder.Configuration.AddJsonFile(userSettings, optional: true, reloadOnChange: false);
}

// Register MemoryMcp.Core services (chunking, embedding, storage, memory service)
builder.Services.AddMemoryMcpCore(builder.Configuration);

// Register MCP server with stdio transport
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Initialize the memory store (create DB schema, directories)
using (var scope = host.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
    await store.InitializeAsync();

    // Startup health check: verify Ollama connectivity and model availability
    var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
    if (embeddingService is OllamaEmbeddingService ollamaService)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var isRunning = await ollamaService.IsAvailableAsync();
        if (!isRunning)
        {
            logger.LogWarning(
                "Ollama is not reachable. Embedding-dependent tools (ingest_memory, update_memory, search_memory) will fail. " +
                "Install Ollama: https://ollama.com/download");
        }
        else
        {
            var modelAvailable = await ollamaService.IsModelAvailableAsync();
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

await host.RunAsync();
