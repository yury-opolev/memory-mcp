using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class WindowsTaskLauncherTests
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory mcp 'launch'");
    private readonly RecordingProcessRunner runner = new();

    private string TaskName => LauncherNames.WindowsTask(PipeNames.Hash(this.dataDirectory));

    [Fact]
    public async Task IsRegistered_RunsSchtasksQuery_WithTaskName()
    {
        var launcher = new WindowsTaskLauncher(this.dataDirectory, this.runner);

        var isRegistered = await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken);

        Assert.True(isRegistered);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("schtasks", process.FileName);
        Assert.Equal(["/Query", "/TN", this.TaskName], process.Arguments);
        Assert.False(process.IsDetached);
    }

    [Fact]
    public async Task Start_RunsSchtasksRun()
    {
        var launcher = new WindowsTaskLauncher(this.dataDirectory, this.runner);

        var isStarted = await launcher.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(isStarted);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("schtasks", process.FileName);
        Assert.Equal(["/Run", "/TN", this.TaskName], process.Arguments);
    }

    [Fact]
    public async Task NonZeroExit_IsFalse()
    {
        this.runner.ExitCode = 1;
        var launcher = new WindowsTaskLauncher(this.dataDirectory, this.runner);

        Assert.False(await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken));
        Assert.False(await launcher.StartAsync(TestContext.Current.CancellationToken));
    }
}
