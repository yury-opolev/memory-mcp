using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using MemoryMcp.Shared.Relay;
using MemoryMcp.Shared.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

public sealed class PipeServiceConnectorTests : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-connector-" + Guid.NewGuid().ToString("N"));
    private readonly string pipeName = "memory-mcp-test-" + Guid.NewGuid().ToString("N");
    private readonly List<IAsyncDisposable> cleanup = [];

    public PipeServiceConnectorTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
        this.WriteSharedEnabled(true);
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
    public async Task ServiceListening_ConnectsWithoutStarting()
    {
        var listener = this.StartListener();
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var result = await this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, Token);

        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task NothingListening_StartsOnce_ThenConnects()
    {
        TestPipeListener? listener = null;
        var launcher = new FakeServiceLauncher("os", isRegistered: true, onStart: () => listener = this.StartListener());

        var result = await this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, Token);

        Assert.NotNull(listener);
        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task OsLauncherNotRegistered_FallsBackToDetached()
    {
        TestPipeListener? listener = null;
        var osLauncher = new FakeServiceLauncher("os", isRegistered: false, onStart: () => listener = this.StartListener());
        var detached = new FakeServiceLauncher("detached", isRegistered: true, onStart: () => listener = this.StartListener());

        var result = await this.CreateConnector(osLauncher, detached).ConnectAsync(ConnectTimeout, Token);

        Assert.NotNull(listener);
        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(0, osLauncher.StartCount);
        Assert.Equal(1, detached.StartCount);
    }

    [Fact]
    public async Task StopMarkerSet_ReturnsStoppedOnPurpose_AndDoesNotStart()
    {
        StopMarker.Set(this.dataDirectory);
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var result = await this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, Token);

        Assert.Equal(ConnectFailure.StoppedOnPurpose, result.Failure);
        Assert.Equal(RelayMessages.StoppedOnPurpose(this.dataDirectory), result.Message);
        Assert.Null(result.Stream);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task StopMarkerSet_ServiceStillListening_Connects()
    {
        StopMarker.Set(this.dataDirectory);
        var listener = this.StartListener();

        var result = await this.CreateConnector(new FakeServiceLauncher("os", isRegistered: true)).ConnectAsync(ConnectTimeout, Token);

        await this.AssertConnectedAsync(result, listener);
    }

    [Fact]
    public async Task SharedModeOffInSettings_ReturnsSharedModeOff_AndDoesNotStart()
    {
        this.WriteSharedEnabled(false);
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var result = await this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, Token);

        Assert.Equal(ConnectFailure.SharedModeOff, result.Failure);
        Assert.Equal(RelayMessages.SharedModeOff, result.Message);
        Assert.Null(result.Stream);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task SharedModeOffAndStopMarkerSet_ReportsSharedModeOffFirst()
    {
        this.WriteSharedEnabled(false);
        StopMarker.Set(this.dataDirectory);
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var result = await this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, Token);

        Assert.Equal(ConnectFailure.SharedModeOff, result.Failure);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task NeverComesUp_ReturnsUnreachableAfterTimeout()
    {
        var launcher = new FakeServiceLauncher("os", isRegistered: true);
        var timeout = TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();

        var result = await this.CreateConnector(launcher).ConnectAsync(timeout, Token);

        stopwatch.Stop();
        Assert.Equal(ConnectFailure.Unreachable, result.Failure);
        Assert.Equal(RelayMessages.Unreachable(5, SharedPaths.LogDirectory(this.dataDirectory)), result.Message);
        Assert.Null(result.Stream);
        Assert.Equal(1, launcher.StartCount);
        Assert.InRange(stopwatch.Elapsed, timeout - TimeSpan.FromMilliseconds(250), timeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Cancelled_WhileWaiting_Throws()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var launcher = new FakeServiceLauncher("os", isRegistered: true, onStart: () => cancellation.CancelAfter(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => this.CreateConnector(launcher).ConnectAsync(ConnectTimeout, cancellation.Token));
    }

    [Fact]
    public async Task ConnectedStream_PendingRead_EndsWhenCancelled()
    {
        var listener = this.StartListener();
        var result = await this.CreateConnector(new FakeServiceLauncher("os", isRegistered: true)).ConnectAsync(ConnectTimeout, Token);
        await using var stream = result.Stream!;
        await listener.Connected;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var read = stream.ReadAsync(new byte[16], cancellation.Token).AsTask();
        await Task.Delay(100, Token);
        Assert.False(read.IsCompleted);
        await cancellation.CancelAsync();

        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), Token));
        Assert.Same(read, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public void ClientPipeOptions_AreAsynchronousAndCurrentUserOnly()
    {
        Assert.True(PipeServiceConnector.ClientPipeOptions.HasFlag(PipeOptions.Asynchronous));
        Assert.True(PipeServiceConnector.ClientPipeOptions.HasFlag(PipeOptions.CurrentUserOnly));
    }

    [Fact]
    public async Task PipeRefusesThisSession_ReturnsAccessDeniedAtOnce_AndStartsNothing()
    {
        var launcher = new FakeServiceLauncher("os", isRegistered: true);
        var openCount = 0;
        var connector = this.CreateConnector(
            [launcher],
            launcherCallTimeout: ServiceLauncherFactory.LauncherCallTimeout,
            openPipe: (_, _, _) =>
            {
                Interlocked.Increment(ref openCount);
                throw new UnauthorizedAccessException("Access to the path is denied.");
            });

        var result = await connector.ConnectAsync(ConnectTimeout, Token);

        Assert.Equal(ConnectFailure.AccessDenied, result.Failure);
        Assert.Equal(RelayMessages.AccessDenied, result.Message);
        Assert.Null(result.Stream);
        Assert.Equal(1, openCount);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task OsLauncherStarts_DetachedIsNotAsked()
    {
        TestPipeListener? listener = null;
        var osLauncher = new FakeServiceLauncher("os", isRegistered: true, onStart: () => listener = this.StartListener());
        var detached = new FakeServiceLauncher("detached", isRegistered: true);

        var result = await this.CreateConnector(osLauncher, detached).ConnectAsync(ConnectTimeout, Token);

        Assert.NotNull(listener);
        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(1, osLauncher.StartCount);
        Assert.Equal(0, detached.StartCount);
    }

    [Fact]
    public async Task OsLauncherRefusesTheStart_FallsBackToDetached()
    {
        TestPipeListener? listener = null;
        var osLauncher = new FakeServiceLauncher("os", isRegistered: true, isStartAccepted: false);
        var detached = new FakeServiceLauncher("detached", isRegistered: true, onStart: () => listener = this.StartListener());

        var result = await this.CreateConnector(osLauncher, detached).ConnectAsync(ConnectTimeout, Token);

        Assert.NotNull(listener);
        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(1, osLauncher.StartCount);
        Assert.Equal(1, detached.StartCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OsLauncherNeverAnswers_FallsBackToDetachedAfterTheCallTimeout(bool isHangingOnIsRegistered, bool isHangingOnStart)
    {
        TestPipeListener? listener = null;
        var osLauncher = new FakeServiceLauncher(
            "os",
            isRegistered: true,
            isHangingOnIsRegistered: isHangingOnIsRegistered,
            isHangingOnStart: isHangingOnStart);
        var detached = new FakeServiceLauncher("detached", isRegistered: true, onStart: () => listener = this.StartListener());
        var connector = this.CreateConnector([osLauncher, detached], TimeSpan.FromMilliseconds(500), PipeServiceConnector.OpenPipeAsync);

        var result = await connector.ConnectAsync(ConnectTimeout, Token);

        Assert.NotNull(listener);
        await this.AssertConnectedAsync(result, listener);
        Assert.Equal(1, detached.StartCount);
    }

    private async Task AssertConnectedAsync(ConnectResult result, TestPipeListener listener)
    {
        Assert.Equal(ConnectFailure.None, result.Failure);
        var stream = Assert.IsAssignableFrom<Stream>(result.Stream);
        this.cleanup.Add(stream);
        await listener.Connected.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // The read is started first: a write on a pipe without a buffer completes only once the other end reads.
        var buffer = new byte[5];
        var read = listener.Pipe.ReadExactlyAsync(buffer, Token).AsTask();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"), Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await stream.FlushAsync(Token);
        await read.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("ping\n", Encoding.UTF8.GetString(buffer));
    }

    private PipeServiceConnector CreateConnector(params IServiceLauncher[] launchers)
    {
        return new PipeServiceConnector(
            ["--MemoryMcp:DataDirectory=" + this.dataDirectory],
            this.dataDirectory,
            this.pipeName,
            launchers,
            NullLogger.Instance);
    }

    private PipeServiceConnector CreateConnector(
        IServiceLauncher[] launchers,
        TimeSpan launcherCallTimeout,
        Func<string, TimeSpan, CancellationToken, Task<Stream>> openPipe)
    {
        return new PipeServiceConnector(
            ["--MemoryMcp:DataDirectory=" + this.dataDirectory],
            this.dataDirectory,
            this.pipeName,
            launchers,
            NullLogger.Instance,
            launcherCallTimeout,
            openPipe);
    }

    private TestPipeListener StartListener()
    {
        var listener = TestPipeListener.Start(this.pipeName);
        this.cleanup.Add(listener);
        return listener;
    }

    private void WriteSharedEnabled(bool isEnabled)
    {
        File.WriteAllText(
            Path.Combine(this.dataDirectory, "settings.json"),
            """{"MemoryMcp":{"Shared":{"Enabled":""" + (isEnabled ? "true" : "false") + "}}}");
    }
}
