using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using MemoryMcp.Shared.Service;
using MemoryMcp.Tests.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

/// <summary>The <c>start</c>, <c>stop</c> and <c>status</c> commands and their control pipe client, against an in-test control pipe.</summary>
public sealed class ControlCommandsTests : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-commands-" + Guid.NewGuid().ToString("N"));
    private readonly string controlPipe = "memory-mcp-test-" + Guid.NewGuid().ToString("N");
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly List<IAsyncDisposable> cleanup = [];

    public ControlCommandsTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
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
    public async Task ControlClient_NothingListening_ReturnsNull()
    {
        Assert.Null(await ControlClient.SendAsync(this.controlPipe, ControlListener.StatusCommand, Token));
    }

    [Fact]
    public async Task ControlClient_SendsTheCommand_AndReadsTheAnswer()
    {
        var listener = this.StartControlListener(processId: 4242);

        var response = await ControlClient.SendAsync(this.controlPipe, ControlListener.StatusCommand, Token);

        Assert.Equal(listener.Response, response);
        Assert.Equal([ControlListener.StatusCommand], listener.Commands);
    }

    [Fact]
    public async Task Status_Running_PrintsTheFields_AndExitsZero()
    {
        this.StartControlListener(processId: 4242);

        var exitCode = await StatusCommand.RunAsync(this.dataDirectory, this.controlPipe, this.output, Token);

        Assert.Equal(0, exitCode);
        var lines = this.Lines();
        Assert.Equal("running", lines[0]);
        Assert.Contains("Version: 9.8.7", lines);
        Assert.Contains("ProcessId: 4242", lines);
        Assert.Contains("Sessions: 2", lines);
        Assert.Contains("Ready: True", lines);
        Assert.Contains("Calls: 12", lines);
        Assert.Contains("RunP95Ms: 20.25", lines);
    }

    [Fact]
    public async Task Status_NotRunning_SaysWhetherStopped_AndExitsThree()
    {
        var exitCode = await StatusCommand.RunAsync(this.dataDirectory, this.controlPipe, this.output, Token);

        Assert.Equal(3, exitCode);
        Assert.Equal(["not running", "stop marker: not set"], this.Lines());

        StopMarker.Set(this.dataDirectory);
        var output = new StringWriter();
        Assert.Equal(3, await StatusCommand.RunAsync(this.dataDirectory, this.controlPipe, output, Token));
        Assert.Contains("stop marker: set", output.ToString());
    }

    [Fact]
    public async Task PrintHash_PrintsOnlyTheHash_AndExitsZero()
    {
        var exitCode = await StatusCommand.PrintHashAsync(this.dataDirectory, this.output);

        Assert.Equal(0, exitCode);
        Assert.Equal(PipeNames.Hash(this.dataDirectory) + Environment.NewLine, this.output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrintHashDataDirectory_ReadsTheDataDirectory_WhereverTheFlagStands(bool isFlagFirst)
    {
        var dataDirectoryArgument = "--MemoryMcp:DataDirectory=" + this.dataDirectory;
        string[] args = isFlagFirst
            ? ["status", StatusCommand.PrintHashOption, dataDirectoryArgument]
            : ["status", dataDirectoryArgument, StatusCommand.PrintHashOption];

        Assert.True(StatusCommand.IsPrintHash(args));
        Assert.Equal(Path.GetFullPath(this.dataDirectory), StatusCommand.PrintHashDataDirectory(args));
    }

    [Fact]
    public void IsPrintHash_WithoutTheFlag_IsFalse()
    {
        Assert.False(StatusCommand.IsPrintHash(["status", "--MemoryMcp:DataDirectory=" + this.dataDirectory]));
    }

    [Fact]
    public async Task Stop_NothingListening_OnlySetsTheMarker()
    {
        var exitCode = await StopCommand.RunAsync(this.dataDirectory, this.controlPipe, this.output, this.error, Token);

        Assert.Equal(0, exitCode);
        Assert.True(StopMarker.Exists(this.dataDirectory));
    }

    [Fact]
    public async Task Stop_Running_SendsStop_AndWaitsForTheServiceToExit()
    {
        var listener = this.StartControlListener(await ExitedProcess.GetIdAsync(Token));

        var exitCode = await StopCommand.RunAsync(this.dataDirectory, this.controlPipe, this.output, this.error, Token);

        Assert.Equal(0, exitCode);
        Assert.Equal([ControlListener.StopCommand], listener.Commands);
    }

    [Fact]
    public async Task Start_ClearsTheMarker_StartsOnce_AndWaitsForStatus()
    {
        StopMarker.Set(this.dataDirectory);
        var launcher = new FakeServiceLauncher("os", isRegistered: true, onStart: () => this.StartControlListener(processId: 4242));

        var exitCode = await StartCommand.RunAsync(this.dataDirectory, this.controlPipe, [launcher], this.output, this.error, StartTimeout, Token);

        Assert.Equal(0, exitCode);
        Assert.False(StopMarker.Exists(this.dataDirectory));
        Assert.Equal(1, launcher.StartCount);
        Assert.Contains("4242", this.output.ToString());
    }

    [Fact]
    public async Task Start_AlreadyRunning_DoesNotStart()
    {
        this.StartControlListener(processId: 4242);
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var exitCode = await StartCommand.RunAsync(this.dataDirectory, this.controlPipe, [launcher], this.output, this.error, StartTimeout, Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Start_UsesFirstRegisteredLauncher()
    {
        var osLauncher = new FakeServiceLauncher("os", isRegistered: false);
        var detached = new FakeServiceLauncher("detached", isRegistered: true, onStart: () => this.StartControlListener(processId: 4242));

        var exitCode = await StartCommand.RunAsync(this.dataDirectory, this.controlPipe, [osLauncher, detached], this.output, this.error, StartTimeout, Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, osLauncher.StartCount);
        Assert.Equal(1, detached.StartCount);
    }

    [Fact]
    public async Task Start_NeverAnswers_ExitsOne_NamingTheLogDirectory()
    {
        var launcher = new FakeServiceLauncher("os", isRegistered: true);

        var exitCode = await StartCommand.RunAsync(this.dataDirectory, this.controlPipe, [launcher], this.output, this.error, TimeSpan.FromSeconds(3), Token);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, launcher.StartCount);
        Assert.Contains(SharedPaths.LogDirectory(this.dataDirectory), this.error.ToString());
    }

    [Fact]
    public async Task Start_LauncherThrows_ExitsOne_WithTheReason()
    {
        var launcher = new FakeServiceLauncher("os", isRegistered: true, onStart: () => throw new InvalidOperationException("cannot run serve"));

        var exitCode = await StartCommand.RunAsync(this.dataDirectory, this.controlPipe, [launcher], this.output, this.error, StartTimeout, Token);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot run serve", this.error.ToString());
    }

    private string[] Lines()
    {
        return this.output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private FakeControlListener StartControlListener(int processId)
    {
        var listener = new FakeControlListener(this.controlPipe, FakeControlListener.CreateResponse(processId));
        this.cleanup.Add(listener);
        return listener;
    }
}
