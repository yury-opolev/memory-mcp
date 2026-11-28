using System.IO.Pipes;
using System.Net.Sockets;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Service;
using MemoryMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class SessionListenerTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly string pipeName = "memory-mcp-test-" + Guid.NewGuid().ToString("N");
    private readonly IMemoryService memory = Substitute.For<IMemoryService>();
    private readonly List<IAsyncDisposable> cleanup = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        for (var index = this.cleanup.Count - 1; index >= 0; index--)
        {
            await this.cleanup[index].DisposeAsync();
        }
    }

    [Fact]
    public async Task Session_ListsTheFiveMemoryTools()
    {
        var listener = this.NewListener(this.FakeServices());
        this.Start(listener);
        var client = await this.ConnectAsync(this.pipeName);

        var tools = await client.Client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(
            ["delete_memory", "get_memory", "ingest_memory", "search_memory", "update_memory"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Session_ToolCall_ReachesTheSharedMemoryService()
    {
        this.memory.GetAsync("x", Arg.Any<CancellationToken>()).Returns((MemoryResult?)null);
        var listener = this.NewListener(this.FakeServices());
        this.Start(listener);
        var client = await this.ConnectAsync(this.pipeName);

        var result = await client.Client.CallToolAsync("get_memory", Arguments("x"), cancellationToken: Token);

        Assert.Equal("Memory not found: x", McpPipeClient.Text(result));
        await this.memory.Received(1).GetAsync("x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoSessions_ShareOneMemoryService_AndActiveSessionsIsTwo()
    {
        this.memory.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((MemoryResult?)null);
        var listener = this.NewListener(this.FakeServices());
        this.Start(listener);
        var first = await McpPipeClient.ConnectAsync(this.pipeName, Token);
        var second = await McpPipeClient.ConnectAsync(this.pipeName, Token);

        await first.Client.CallToolAsync("get_memory", Arguments("a"), cancellationToken: Token);
        await second.Client.CallToolAsync("get_memory", Arguments("b"), cancellationToken: Token);

        Assert.Equal(2, listener.ActiveSessions);
        await this.memory.Received(1).GetAsync("a", Arg.Any<CancellationToken>());
        await this.memory.Received(1).GetAsync("b", Arg.Any<CancellationToken>());

        await first.DisposeAsync();
        await second.DisposeAsync();
        await Eventually.WaitUntilAsync(() => listener.ActiveSessions == 0, Token);
    }

    [Fact]
    public async Task Handshake_IsAnsweredWhileStoreIsStillStarting()
    {
        var laneMemory = Substitute.For<IMemoryService>();
        laneMemory.GetAsync("x", Arg.Any<CancellationToken>()).Returns((MemoryResult?)null);
        var release = new TaskCompletionSource<StoreLane>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(release.Task);
        var metrics = new CallMetrics();
        var services = this.PooledServices(factory, metrics, out var pool);
        var listener = this.NewListener(services);
        this.Start(listener);
        pool.Start();

        var client = await this.ConnectAsync(this.pipeName);
        var tools = await client.Client.ListToolsAsync(cancellationToken: Token);
        Assert.Equal(5, tools.Count);

        var call = client.Client.CallToolAsync("get_memory", Arguments("x"), cancellationToken: Token).AsTask();
        await Eventually.WaitUntilAsync(() => metrics.Snapshot().QueueDepth == 1, Token);
        Assert.False(call.IsCompleted);
        Assert.False(pool.IsReady);

        release.SetResult(new StoreLane(laneMemory, Substitute.For<IHybridMemorySearch>(), Substitute.For<IAsyncDisposable>()));

        var result = await call.WaitAsync(TestTimeout, Token);
        Assert.Equal("Memory not found: x", McpPipeClient.Text(result));
        Assert.True(pool.IsReady);
        await laneMemory.Received(1).GetAsync("x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolCall_WhenStartupFailed_ReturnsErrorWithStartupMessage_AndSessionStaysOpen()
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StoreLane>(new InvalidOperationException("the disk is on fire")));
        var services = this.PooledServices(factory, new CallMetrics(), out var pool);
        var listener = this.NewListener(services);
        this.Start(listener);
        pool.Start();
        var client = await this.ConnectAsync(this.pipeName);

        var result = await client.Client.CallToolAsync("get_memory", Arguments("x"), cancellationToken: Token);

        Assert.True(result.IsError);
        Assert.Contains("the disk is on fire", McpPipeClient.Text(result), StringComparison.Ordinal);
        Assert.Equal("the disk is on fire", pool.StartupError);
        var tools = await client.Client.ListToolsAsync(cancellationToken: Token);
        Assert.Equal(5, tools.Count);
        Assert.Equal(1, listener.ActiveSessions);
    }

    [Fact]
    public async Task Session_WithServiceRegistrations_RunsToolCallsOnAStoreLane()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-host-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MemoryMcp:DataDirectory"] = dataDirectory })
            .Build();
        var laneMemory = Substitute.For<IMemoryService>();
        laneMemory.GetAsync("x", Arg.Any<CancellationToken>()).Returns((MemoryResult?)null);
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StoreLane(laneMemory, Substitute.For<IHybridMemorySearch>(), Substitute.For<IAsyncDisposable>())));
        var collection = new ServiceCollection();
        collection.AddLogging();
        MemoryServiceHost.AddServices(collection, configuration, dataDirectory);
        collection.AddSingleton(factory);
        var services = collection.BuildServiceProvider();
        this.cleanup.Add(services);

        Assert.IsType<PooledMemoryService>(services.GetRequiredService<IMemoryService>());
        Assert.IsType<PooledHybridMemorySearch>(services.GetRequiredService<IHybridMemorySearch>());
        services.GetRequiredService<StorePool>().Start();
        this.Start(services.GetRequiredService<SessionListener>());
        var client = await this.ConnectAsync(PipeNames.Session(dataDirectory));

        var result = await client.Client.CallToolAsync("get_memory", Arguments("x"), cancellationToken: Token);

        Assert.Equal("Memory not found: x", McpPipeClient.Text(result));
        await laneMemory.Received(1).GetAsync("x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Listener_CreatesPipesWithCurrentUserOnly()
    {
        var requested = new List<PipeOptions>();
        var listener = new SessionListener(
            this.pipeName,
            this.FakeServices(),
            NullLoggerFactory.Instance,
            (name, options) =>
            {
                lock (requested)
                {
                    requested.Add(options);
                }

                return SessionListener.CreateServerPipe(name, options);
            });
        this.Start(listener);
        var client = await this.ConnectAsync(this.pipeName);
        await client.Client.ListToolsAsync(cancellationToken: Token);

        PipeOptions[] seen;
        lock (requested)
        {
            seen = [.. requested];
        }

        Assert.True(seen.Length >= 2);
        Assert.All(seen, options => Assert.Equal(PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, options));
    }

    [Fact]
    public void Listener_WhenThePipeCannotBeCreated_FailsAtOnce()
    {
        var listener = new SessionListener(
            this.pipeName,
            this.FakeServices(),
            NullLoggerFactory.Instance,
            (_, _) => throw new UnauthorizedAccessException("not allowed"));

        var run = listener.RunAsync(Token);

        Assert.True(run.IsFaulted);
        Assert.IsType<UnauthorizedAccessException>(run.Exception!.InnerException);
    }

    [Fact]
    public async Task Session_ClientLeavesMidHandshake_ListenerKeepsServing()
    {
        var listener = this.NewListener(this.FakeServices());
        this.Start(listener);
        await using (var rude = await McpPipeClient.ConnectPipeAsync(this.pipeName, Token))
        {
            await rude.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initia"u8.ToArray(), Token);
        }

        await using (var garbage = await McpPipeClient.ConnectPipeAsync(this.pipeName, Token))
        {
            await garbage.WriteAsync("this is not json\n"u8.ToArray(), Token);
        }

        var client = await this.ConnectAsync(this.pipeName);
        var tools = await client.Client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(5, tools.Count);
        await Eventually.WaitUntilAsync(() => listener.ActiveSessions == 1, Token);
    }

    [Fact]
    public async Task Listener_StartsWhenAStaleSocketFileExists()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Named pipes are Unix domain sockets only on Unix.");
        var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + this.pipeName);
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(socketPath));
        }

        Assert.True(File.Exists(socketPath));
        var listener = this.NewListener(this.FakeServices());
        this.Start(listener);
        var client = await this.ConnectAsync(this.pipeName);

        var tools = await client.Client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(5, tools.Count);
    }

    private static Dictionary<string, object?> Arguments(string id)
    {
        return new Dictionary<string, object?> { ["id"] = id };
    }

    private IServiceProvider FakeServices()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(this.memory);
        collection.AddSingleton(Substitute.For<IHybridMemorySearch>());
        collection.AddSingleton(Options.Create(new MemoryMcpOptions()));
        collection.AddMcpServer().WithToolsFromAssembly(typeof(MemoryTools).Assembly);
        var services = collection.BuildServiceProvider();
        this.cleanup.Add(services);
        return services;
    }

    private IServiceProvider PooledServices(IStoreLaneFactory factory, CallMetrics metrics, out StorePool pool)
    {
        var storePool = new StorePool(factory, 1, metrics, NullLogger<StorePool>.Instance, TimeProvider.System);
        this.cleanup.Add(storePool);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(storePool);
        collection.AddSingleton<IMemoryService, PooledMemoryService>();
        collection.AddSingleton<IHybridMemorySearch, PooledHybridMemorySearch>();
        collection.AddSingleton(Options.Create(new MemoryMcpOptions()));
        collection.AddMcpServer().WithToolsFromAssembly(typeof(MemoryTools).Assembly);
        var services = collection.BuildServiceProvider();
        this.cleanup.Add(services);
        pool = storePool;
        return services;
    }

    private SessionListener NewListener(IServiceProvider services)
    {
        return new SessionListener(this.pipeName, services, services.GetRequiredService<ILoggerFactory>());
    }

    private void Start(SessionListener listener)
    {
        this.cleanup.Add(RunningListener.Start(listener.RunAsync));
    }

    private async Task<McpPipeClient> ConnectAsync(string pipe)
    {
        var client = await McpPipeClient.ConnectAsync(pipe, Token);
        this.cleanup.Add(client);
        return client;
    }
}
