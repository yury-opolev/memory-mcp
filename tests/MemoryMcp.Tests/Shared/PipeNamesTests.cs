using MemoryMcp.Shared;
using Xunit;

namespace MemoryMcp.Tests.Shared;

public class PipeNamesTests
{
    private static string NewDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "memory-mcp-pipe-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Session_SameDirectorySpelledDifferently_SameName()
    {
        var directory = NewDirectory();
        var expected = PipeNames.Session(directory);

        Assert.Equal(expected, PipeNames.Session(directory + Path.DirectorySeparatorChar));

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, directory);
        Assert.Equal(expected, PipeNames.Session(relative));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, PipeNames.Session(directory.ToUpperInvariant()));
        }
    }

    [Fact]
    public void Session_DifferentDirectories_DifferentNames()
    {
        Assert.NotEqual(PipeNames.Session(NewDirectory()), PipeNames.Session(NewDirectory()));
    }

    [Fact]
    public void Session_HasExpectedShape()
    {
        Assert.Matches("^memory-mcp-[0-9a-f]{16}$", PipeNames.Session(NewDirectory()));
    }

    [Fact]
    public void Control_IsSessionPlusSuffix()
    {
        var directory = NewDirectory();

        Assert.Equal(PipeNames.Session(directory) + "-control", PipeNames.Control(directory));
    }

    [Fact]
    public void Hash_IsFirstEightHexOfSession()
    {
        var directory = NewDirectory();
        var session = PipeNames.Session(directory);

        Assert.Equal(session["memory-mcp-".Length..][..8], PipeNames.Hash(directory));
    }
}
