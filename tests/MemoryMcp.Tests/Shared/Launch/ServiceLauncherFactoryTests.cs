using System.Runtime.InteropServices;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class ServiceLauncherFactoryTests
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-factory");
    private readonly RecordingProcessRunner runner = new();

    public static TheoryData<string, Type> OsLaunchers => new()
    {
        { "WINDOWS", typeof(WindowsTaskLauncher) },
        { "LINUX", typeof(SystemdUserLauncher) },
        { "OSX", typeof(LaunchdLauncher) },
    };

    [Theory]
    [MemberData(nameof(OsLaunchers))]
    public void Create_OsLauncherFirst_DetachedLast(string platform, Type osLauncher)
    {
        var launchers = ServiceLauncherFactory.Create(this.dataDirectory, this.runner, OSPlatform.Create(platform));

        Assert.Collection(
            launchers,
            first => Assert.IsType(osLauncher, first),
            last => Assert.IsType<DetachedProcessLauncher>(last));
        Assert.Empty(this.runner.Processes);
    }

    [Fact]
    public void Create_OtherPlatform_OnlyDetached()
    {
        var launchers = ServiceLauncherFactory.Create(this.dataDirectory, this.runner, OSPlatform.FreeBSD);

        Assert.IsType<DetachedProcessLauncher>(Assert.Single(launchers));
    }

    [Fact]
    public void Create_ForThisMachine_EndsWithDetached()
    {
        var launchers = ServiceLauncherFactory.Create(this.dataDirectory, this.runner);

        Assert.IsType<DetachedProcessLauncher>(launchers[^1]);
    }
}
