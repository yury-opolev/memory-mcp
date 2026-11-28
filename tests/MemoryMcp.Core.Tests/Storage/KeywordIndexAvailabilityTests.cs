using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// The SQLCipher bundle used by this test project predates FTS5 <c>contentless_delete</c>: the keyword
/// index must report itself unavailable and every other store operation must keep working.
/// (The index itself is exercised on SQLite3 Multiple Ciphers in MemoryMcp.Tests and in the Cortex
/// Agent.Host tests.)
/// </summary>
public sealed class KeywordIndexAvailabilityTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_kw_{Guid.NewGuid():N}");
    private readonly SqliteVecMemoryStore store;

    public KeywordIndexAvailabilityTests()
    {
        Directory.CreateDirectory(this.tempDir);
        var options = new MemoryMcpOptions { DataDirectory = this.tempDir, Ollama = new OllamaOptions { Dimensions = 4 } };
        this.store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    [Fact]
    public async Task OldSqliteEngine_KeywordIndexUnavailable_StoreStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.NotNull(this.store.KeywordIndexUnavailableReason);

        await this.store.InitializeAsync(ct);
        if (this.store.KeywordIndexUnavailableReason is null)
        {
            Assert.Skip("This SQLite engine supports the keyword index.");
        }

        var now = DateTimeOffset.UtcNow;
        await this.store.StoreMemoryAsync("m1", "train to TownB", [new ChunkRecord { MemoryId = "m1", ChunkIndex = 0, Length = 14, CreatedAt = now, UpdatedAt = now, Title = "trip" }], [[1, 0, 0, 0]], ct);
        await this.store.UpdateMetadataAsync("m1", "new title", null, now, ct);

        Assert.Empty(await this.store.KeywordSearchAsync("body : (\"train\")", 5, MemorySearchFilter.None, ct));
        Assert.Equal(0, await this.store.RebuildKeywordIndexAsync(ct));
        Assert.Equal("new title", (await this.store.GetMemoryAsync("m1", ct))!.Title);
        Assert.True(await this.store.DeleteMemoryAsync("m1", ct));
    }
}
