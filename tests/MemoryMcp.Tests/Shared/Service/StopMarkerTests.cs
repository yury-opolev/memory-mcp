using MemoryMcp.Shared;
using MemoryMcp.Shared.Service;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public sealed class StopMarkerTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-stop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dataDirectory))
        {
            Directory.Delete(this.dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Set_Exists_Clear_RoundTrip()
    {
        Assert.False(StopMarker.Exists(this.dataDirectory));

        StopMarker.Set(this.dataDirectory);

        Assert.True(StopMarker.Exists(this.dataDirectory));
        Assert.True(File.Exists(SharedPaths.StopMarker(this.dataDirectory)));

        StopMarker.Clear(this.dataDirectory);

        Assert.False(StopMarker.Exists(this.dataDirectory));
        StopMarker.Clear(this.dataDirectory);
    }
}
