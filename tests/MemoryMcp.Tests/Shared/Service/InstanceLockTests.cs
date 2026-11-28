using MemoryMcp.Shared;
using MemoryMcp.Shared.Service;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class InstanceLockTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dataDirectory))
        {
            Directory.Delete(this.dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryAcquire_Twice_SecondReturnsNull()
    {
        using var first = InstanceLock.TryAcquire(this.dataDirectory);

        var second = InstanceLock.TryAcquire(this.dataDirectory);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_WritesProcessIdAndVersion_ReadableWhileHeld()
    {
        using var held = InstanceLock.TryAcquire(this.dataDirectory);

        string content;
        using (var reader = new FileStream(SharedPaths.ServiceLock(this.dataDirectory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var text = new StreamReader(reader))
        {
            content = text.ReadToEnd();
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), lines[0]);
        Assert.Equal(MemoryServiceHost.Version, lines[1]);
    }

    [Fact]
    public void TryAcquire_AfterDispose_Succeeds()
    {
        var first = InstanceLock.TryAcquire(this.dataDirectory);
        Assert.NotNull(first);
        first.Dispose();

        using var second = InstanceLock.TryAcquire(this.dataDirectory);

        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_LeftoverFileWithNoHolder_Succeeds()
    {
        Directory.CreateDirectory(this.dataDirectory);
        File.WriteAllText(SharedPaths.ServiceLock(this.dataDirectory), "99999999\nold-version-with-a-long-tail\n");

        using var acquired = InstanceLock.TryAcquire(this.dataDirectory);

        Assert.NotNull(acquired);
        using var reader = new FileStream(SharedPaths.ServiceLock(this.dataDirectory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        var firstLine = text.ReadLine();
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), firstLine);
        Assert.DoesNotContain("old-version", text.ReadToEnd(), StringComparison.Ordinal);
    }
}
