using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class ServiceExecutableTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-executable-" + Guid.NewGuid().ToString("N"));

    public ServiceExecutableTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(this.dataDirectory, recursive: true);
    }

    [Fact]
    public void PathFilePresent_ReturnsItsPath()
    {
        var executable = Path.Combine(this.dataDirectory, "service 1", "MemoryMcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(SharedPaths.ServicePathFile(this.dataDirectory), executable + Environment.NewLine);

        Assert.Equal(executable, ServiceExecutable.Resolve(this.dataDirectory));
    }

    [Fact]
    public void PathFileMissingOrDangling_ReturnsProcessPath()
    {
        Assert.Equal(Environment.ProcessPath, ServiceExecutable.Resolve(this.dataDirectory));

        File.WriteAllText(SharedPaths.ServicePathFile(this.dataDirectory), Path.Combine(this.dataDirectory, "gone", "MemoryMcp.exe"));

        Assert.Equal(Environment.ProcessPath, ServiceExecutable.Resolve(this.dataDirectory));
    }
}
