using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Hosting;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class MemoryServiceHostTests : IDisposable
{
    private const string ContentMarker = "content-marker-7f3a91";
    private const string TitleMarker = "title-marker-52bc08";
    private const string QueryMarker = "query-marker-e41d6c";
    private const string UpdatedContentMarker = "updated-marker-9a0b27";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-service-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(this.dataDirectory))
        {
            Directory.Delete(this.dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionListenerFailure_WithASessionOpen_StopsTheHost()
    {
        using var fileLogs = new FileLoggerProvider(SharedPaths.LogDirectory(this.dataDirectory));
        var builder = ServiceCommand.CreateHostBuilder(this.Configuration(), [], this.dataDirectory, fileLogs);
        builder.Services.AddSingleton(NewLaneFactory(Substitute.For<IMemoryService>(), Substitute.For<IHybridMemorySearch>()));
        var created = 0;
        builder.Services.AddSingleton(provider => new SessionListener(
            PipeNames.Session(this.dataDirectory),
            provider,
            provider.GetRequiredService<ILoggerFactory>(),
            (name, options) => Interlocked.Increment(ref created) == 1
                ? SessionListener.CreateServerPipe(name, options)
                : throw new IOException("no more pipes")));
        using var host = builder.Build();
        await host.StartAsync(Token);
        try
        {
            await using var openSession = await McpPipeClient.ConnectPipeAsync(PipeNames.Session(this.dataDirectory), Token);

            await host.WaitForShutdownAsync(Token).WaitAsync(TestTimeout, Token);

            var failure = host.Services.GetRequiredService<MemoryServiceHost>().ListenerFailure;
            Assert.IsType<IOException>(failure);
        }
        finally
        {
            await host.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Logging:LogLevel:Default", "Trace")]
    [InlineData("Logging:LogLevel:ModelContextProtocol", "Trace")]
    [InlineData("Logging:LogLevel:ModelContextProtocol.Server", "Debug")]
    public async Task ServiceLogging_KeepsQueriesAndMemoryContentOutOfTheLogFile(string? levelKey, string? level)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.IngestAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new IngestResult { Success = true, MemoryId = "m1" });
        memory.GetAsync("m1", Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = "m1", Title = TitleMarker, Content = ContentMarker });
        memory.UpdateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = "m1", Title = TitleMarker, Content = UpdatedContentMarker });
        var search = Substitute.For<IHybridMemorySearch>();
        search.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HybridSearchResult(
                [new HybridSearchHit(new SearchResult { MemoryId = "m1", Title = TitleMarker, Content = ContentMarker }, MemoryMatchKind.Keyword, 1, 1, null, null)],
                MemorySearchMode.Keyword,
                null,
                null));

        var fileLogs = new FileLoggerProvider(SharedPaths.LogDirectory(this.dataDirectory));
        var builder = ServiceCommand.CreateHostBuilder(this.Configuration(levelKey, level), [], this.dataDirectory, fileLogs);
        builder.Services.AddSingleton(NewLaneFactory(memory, search));
        using (var host = builder.Build())
        {
            await host.StartAsync(Token);
            await using (var client = await McpPipeClient.ConnectAsync(PipeNames.Session(this.dataDirectory), Token))
            {
                var ingested = await client.Client.CallToolAsync(
                    "ingest_memory",
                    new Dictionary<string, object?> { ["content"] = ContentMarker, ["title"] = TitleMarker },
                    cancellationToken: Token);
                var fetched = await client.Client.CallToolAsync("get_memory", new Dictionary<string, object?> { ["id"] = "m1" }, cancellationToken: Token);
                var updated = await client.Client.CallToolAsync(
                    "update_memory",
                    new Dictionary<string, object?> { ["id"] = "m1", ["content"] = UpdatedContentMarker },
                    cancellationToken: Token);
                var found = await client.Client.CallToolAsync("search_memory", new Dictionary<string, object?> { ["query"] = QueryMarker }, cancellationToken: Token);

                Assert.Contains("m1", McpPipeClient.Text(ingested), StringComparison.Ordinal);
                Assert.Contains(ContentMarker, McpPipeClient.Text(fetched), StringComparison.Ordinal);
                Assert.Contains(UpdatedContentMarker, McpPipeClient.Text(updated), StringComparison.Ordinal);
                Assert.Contains(ContentMarker, McpPipeClient.Text(found), StringComparison.Ordinal);
            }

            await host.StopAsync(Token);
        }

        fileLogs.Dispose();
        var log = string.Concat(Directory.GetFiles(SharedPaths.LogDirectory(this.dataDirectory)).Select(File.ReadAllText));
        Assert.Contains("Session opened", log, StringComparison.Ordinal);
        Assert.Contains("Memory service stopped", log, StringComparison.Ordinal);
        Assert.DoesNotContain(ContentMarker, log, StringComparison.Ordinal);
        Assert.DoesNotContain(TitleMarker, log, StringComparison.Ordinal);
        Assert.DoesNotContain(QueryMarker, log, StringComparison.Ordinal);
        Assert.DoesNotContain(UpdatedContentMarker, log, StringComparison.Ordinal);
    }

    private static IStoreLaneFactory NewLaneFactory(IMemoryService memory, IHybridMemorySearch search)
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StoreLane(memory, search, Substitute.For<IAsyncDisposable>())));
        return factory;
    }

    private IConfigurationRoot Configuration(string? levelKey = null, string? level = null)
    {
        // Without a "Logging" section the service uses the logging defaults; a level key lowers it the way a
        // user's settings.json could.
        var values = new Dictionary<string, string?>
        {
            ["MemoryMcp:DataDirectory"] = this.dataDirectory,
            ["MemoryMcp:Encryption:Enabled"] = "false",
        };
        if (levelKey is not null)
        {
            values[levelKey] = level;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
