using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class LauncherNamesTests
{
    [Fact]
    public void Names_FollowTheInstalledPatterns()
    {
        Assert.Equal("MemoryMcp-0123abcd", LauncherNames.WindowsTask("0123abcd"));
        Assert.Equal("memory-mcp-0123abcd.service", LauncherNames.SystemdUnit("0123abcd"));
        Assert.Equal("io.github.memory-mcp.0123abcd", LauncherNames.LaunchdLabel("0123abcd"));
    }
}
