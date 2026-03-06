using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

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
}

await host.RunAsync();
