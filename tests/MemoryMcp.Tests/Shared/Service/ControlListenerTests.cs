using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Shared.Service;
using MemoryMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class ControlListenerTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly string sessionPipe = "memory-mcp-test-" + Guid.NewGuid().ToString("N");
    private readonly string controlPipe = "memory-mcp-test-" + Guid.NewGuid().ToString("N");
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-control-" + Guid.NewGuid().ToString("N"));
    private readonly IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly TaskCompletionSource shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CallMetrics metrics = new();
    private readonly List<IAsyncDisposable> cleanup = [];

    public ControlListenerTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
        this.lifetime.When(application => application.StopApplication()).Do(_ => this.shutdownRequested.TrySetResult());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        for (var index = this.cleanup.Count - 1; index >= 0; index--)
        {
            await this.cleanup[index].DisposeAsync();
        }

        Directory.Delete(this.dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Status_ReturnsVersionSessionsReadinessAndMetrics()
    {
        var pool = await this.StartReadyPoolAsync();
        await pool.RunAsync("get_memory", _ => Task.FromResult(0), Token);
        var sessions = this.StartSessionListener();
        var client = await McpPipeClient.ConnectAsync(this.sessionPipe, Token);
        this.cleanup.Add(client);
        this.StartControlListener(pool, sessions);

        var answer = await this.SendAsync("status");

        var response = ControlResponse.FromJson(answer!);
        Assert.NotNull(response);
        Assert.Equal(MemoryServiceHost.Version, response.Version);
        Assert.Equal(Environment.ProcessId, response.ProcessId);
        Assert.Equal(1, response.Sessions);
        Assert.True(response.Ready);
        Assert.Null(response.StartupError);
        Assert.Equal(2, response.Metrics.Calls); // the warm-up lease and the call above
        Assert.False(StopMarker.Exists(this.dataDirectory));
        this.lifetime.DidNotReceive().StopApplication();
    }

    [Fact]
    public async Task Status_WhenStartupFailed_ReportsNotReadyAndTheError()
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromException<StoreLane>(new InvalidOperationException("no database")));
        var pool = this.NewPool(factory);
        pool.Start();
        await Assert.ThrowsAsync<ServiceNotReadyException>(() => pool.RunAsync("get_memory", _ => Task.FromResult(0), Token));
        this.StartControlListener(pool, this.StartSessionListener());

        var response = ControlResponse.FromJson((await this.SendAsync("status"))!);

        Assert.NotNull(response);
        Assert.False(response.Ready);
        Assert.Equal("no database", response.StartupError);
    }

    [Fact]
    public async Task Stop_SetsStopMarker_AndRequestsShutdown()
    {
        var pool = await this.StartReadyPoolAsync();
        this.StartControlListener(pool, this.StartSessionListener());

        var answer = await this.SendAsync("stop");

        Assert.NotNull(ControlResponse.FromJson(answer!));
        Assert.True(StopMarker.Exists(this.dataDirectory));
        await this.shutdownRequested.Task.WaitAsync(TestTimeout, Token);
        this.lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task Stop_WhenTheMarkerCannotBeWritten_AnswersWithTheError_AndKeepsRunning()
    {
        // A directory where the marker file should be makes writing it fail.
        Directory.CreateDirectory(Path.Combine(this.dataDirectory, "service.stopped"));
        var pool = await this.StartReadyPoolAsync();
        this.StartControlListener(pool, this.StartSessionListener());

        var answer = await this.SendAsync("stop");

        var response = ControlResponse.FromJson(answer!);
        Assert.NotNull(response);
        Assert.NotNull(response.Error);
        Assert.Contains("stop marker could not be written", response.Error, StringComparison.Ordinal);
        Assert.NotNull(ControlResponse.FromJson((await this.SendAsync("status"))!));
        this.lifetime.DidNotReceive().StopApplication();
    }

    [Fact]
    public async Task UnknownCommand_ClosesWithoutAnswer_AndListenerKeepsServing()
    {
        var pool = await this.StartReadyPoolAsync();
        this.StartControlListener(pool, this.StartSessionListener());

        var unknown = await this.SendAsync("reboot");
        var status = await this.SendAsync("status");

        Assert.Null(unknown);
        Assert.NotNull(ControlResponse.FromJson(status!));
        Assert.False(StopMarker.Exists(this.dataDirectory));
        this.lifetime.DidNotReceive().StopApplication();
    }

    private async Task<string?> SendAsync(string command)
    {
        await using var pipe = await McpPipeClient.ConnectPipeAsync(this.controlPipe, Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), Token);
        await pipe.FlushAsync(Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadLineAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
    }

    private StorePool NewPool(IStoreLaneFactory factory)
    {
        var pool = new StorePool(factory, 1, this.metrics, NullLogger<StorePool>.Instance, TimeProvider.System);
        this.cleanup.Add(pool);
        return pool;
    }

    private async Task<StorePool> StartReadyPoolAsync()
    {
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StoreLane(Substitute.For<IMemoryService>(), Substitute.For<IHybridMemorySearch>(), Substitute.For<IAsyncDisposable>())));
        var pool = this.NewPool(factory);
        pool.Start();
        (await pool.LeaseAsync("warm-up", Token)).Dispose();
        return pool;
    }

    private SessionListener StartSessionListener()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(Substitute.For<IMemoryService>());
        collection.AddSingleton(Substitute.For<IHybridMemorySearch>());
        collection.AddSingleton(Options.Create(new MemoryMcpOptions()));
        collection.AddMcpServer().WithToolsFromAssembly(typeof(MemoryTools).Assembly);
        var services = collection.BuildServiceProvider();
        this.cleanup.Add(services);
        var listener = new SessionListener(this.sessionPipe, services, NullLoggerFactory.Instance);
        this.cleanup.Add(RunningListener.Start(listener.RunAsync));
        return listener;
    }

    private void StartControlListener(StorePool pool, SessionListener sessions)
    {
        var listener = new ControlListener(
            this.controlPipe,
            this.dataDirectory,
            pool,
            this.metrics,
            sessions,
            this.lifetime,
            NullLogger<ControlListener>.Instance);
        this.cleanup.Add(RunningListener.Start(listener.RunAsync));
    }
}
