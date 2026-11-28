using MemoryMcp.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MemoryMcp.Hosting;

/// <summary>The behaviour before shared mode: one stdio MCP server per process that owns the database.</summary>
public static class StandaloneServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Load user settings from data directory (survives rebuilds)
        ServerConfiguration.AddUserSettings(builder.Configuration);

        // Register MemoryMcp.Core services (chunking, embedding, storage, memory service)
        builder.Services.AddMemoryMcpCore(builder.Configuration);

        // Register MCP server with stdio transport
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();

        // Initialize the memory store (create DB schema, directories)
        await StoreStartup.RunAsync(host.Services, CancellationToken.None).ConfigureAwait(false);

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
