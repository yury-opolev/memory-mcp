using System.Runtime.InteropServices;
using MemoryMcp.Core.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Security;

/// <summary>
/// Tests for DpapiKeyStore. Only runs on Windows.
/// </summary>
public class DpapiKeyStoreTests : IDisposable
{
    private readonly string tempDir;

    public DpapiKeyStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_keytest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            try { Directory.Delete(this.tempDir, recursive: true); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetOrCreateKeyAsync_CreatesKeyFile()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // DPAPI is Windows-only
        }

        var logger = Substitute.For<ILogger<DpapiKeyStore>>();
        var store = new DpapiKeyStore(this.tempDir, logger);

        var key = await store.GetOrCreateKeyAsync();

        Assert.Equal(32, key.Length);
        Assert.True(File.Exists(Path.Combine(this.tempDir, "encryption.key")));
    }

    [Fact]
    public async Task GetOrCreateKeyAsync_ReturnsSameKeyOnSubsequentCalls()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var logger = Substitute.For<ILogger<DpapiKeyStore>>();
        var store = new DpapiKeyStore(this.tempDir, logger);

        var key1 = await store.GetOrCreateKeyAsync();
        var key2 = await store.GetOrCreateKeyAsync();

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetOrCreateKeyAsync_NewInstance_LoadsExistingKey()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var logger = Substitute.For<ILogger<DpapiKeyStore>>();

        var store1 = new DpapiKeyStore(this.tempDir, logger);
        var key1 = await store1.GetOrCreateKeyAsync();

        // Create a new instance pointing to the same directory
        var store2 = new DpapiKeyStore(this.tempDir, logger);
        var key2 = await store2.GetOrCreateKeyAsync();

        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetOrCreateKeyAsync_DifferentDirectories_DifferentKeys()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var logger = Substitute.For<ILogger<DpapiKeyStore>>();

        var dir2 = Path.Combine(Path.GetTempPath(), $"memorymcp_keytest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir2);

        try
        {
            var store1 = new DpapiKeyStore(this.tempDir, logger);
            var store2 = new DpapiKeyStore(dir2, logger);

            var key1 = await store1.GetOrCreateKeyAsync();
            var key2 = await store2.GetOrCreateKeyAsync();

            Assert.NotEqual(key1, key2);
        }
        finally
        {
            try { Directory.Delete(dir2, recursive: true); } catch { }
        }
    }
}
