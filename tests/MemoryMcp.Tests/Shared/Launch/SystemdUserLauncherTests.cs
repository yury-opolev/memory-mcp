using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class SystemdUserLauncherTests
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory mcp 'launch'");
    private readonly RecordingProcessRunner runner = new();

    private string UnitName => LauncherNames.SystemdUnit(PipeNames.Hash(this.dataDirectory));

    [Fact]
    public async Task IsRegistered_RunsSystemctlCat_WithUnitName()
    {
        var launcher = new SystemdUserLauncher(this.dataDirectory, this.runner);

        var isRegistered = await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken);

        Assert.True(isRegistered);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("systemctl", process.FileName);
        Assert.Equal(["--user", "cat", this.UnitName], process.Arguments);
        Assert.False(process.IsDetached);
    }

    [Fact]
    public async Task Start_RunsSystemctlStart()
    {
        var launcher = new SystemdUserLauncher(this.dataDirectory, this.runner);

        var isStarted = await launcher.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(isStarted);
        var process = Assert.Single(this.runner.Processes);
        Assert.Equal("systemctl", process.FileName);
        Assert.Equal(["--user", "start", this.UnitName], process.Arguments);
    }

    [Fact]
    public async Task NonZeroExit_IsFalse()
    {
        this.runner.ExitCode = 4;
        var launcher = new SystemdUserLauncher(this.dataDirectory, this.runner);

        Assert.False(await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken));
        Assert.False(await launcher.StartAsync(TestContext.Current.CancellationToken));
    }
}
