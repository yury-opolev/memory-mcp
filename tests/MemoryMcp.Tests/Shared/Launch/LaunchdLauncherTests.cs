using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class LaunchdLauncherTests
{
    private const uint UserId = 501;

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory mcp 'launch'");
    private readonly RecordingProcessRunner runner = new();

    private string Target => $"gui/{UserId}/{LauncherNames.LaunchdLabel(PipeNames.Hash(this.dataDirectory))}";

    [Fact]
    public async Task IsRegistered_RunsLaunchctlPrint_WithServiceTarget()
    {
        var launcher = new LaunchdLauncher(this.dataDirectory, this.runner, () => UserId);

        var isRegistered = await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken);

        Assert.True(isRegistered);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("launchctl", process.FileName);
        Assert.Equal(["print", this.Target], process.Arguments);
        Assert.False(process.IsDetached);
    }

    [Fact]
    public async Task Start_RunsLaunchctlKickstart()
    {
        var launcher = new LaunchdLauncher(this.dataDirectory, this.runner, () => UserId);

        var isStarted = await launcher.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(isStarted);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("launchctl", process.FileName);
        Assert.Equal(["kickstart", this.Target], process.Arguments);
    }

    [Fact]
    public async Task NonZeroExit_IsFalse()
    {
        this.runner.ExitCode = 113;
        var launcher = new LaunchdLauncher(this.dataDirectory, this.runner, () => UserId);

        Assert.False(await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken));
        Assert.False(await launcher.StartAsync(TestContext.Current.CancellationToken));
    }
}
