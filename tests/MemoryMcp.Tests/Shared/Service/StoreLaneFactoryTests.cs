using MemoryMcp.Shared.Service;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class StoreLaneFactoryTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-lane-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.dataDirectory))
        {
            Directory.Delete(this.dataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_OnARealDatabase_LaneServesACallThatNeedsNoEmbedding(bool runStartup)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemoryMcp:DataDirectory"] = this.dataDirectory,
                ["MemoryMcp:Encryption:Enabled"] = "false",
                ["MemoryMcp:Ollama:Endpoint"] = "http://127.0.0.1:9",
            })
            .Build();
        var factory = new StoreLaneFactory(configuration, NullLoggerFactory.Instance);

        var lane = await factory.CreateAsync(runStartup, Token);
        try
        {
            var missing = await lane.Memory.GetAsync(Guid.NewGuid().ToString(), Token);

            Assert.Null(missing);
            Assert.True(File.Exists(Path.Combine(this.dataDirectory, "memory.db")));
        }
        finally
        {
            await lane.Owner.DisposeAsync();
        }
    }
}
