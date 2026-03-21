using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Security;

/// <summary>
/// Tests that SqliteVecMemoryStore works correctly with encryption enabled
/// (SQLCipher for the database, AES-256-GCM for content files).
/// </summary>
public class EncryptedStoreTests : IDisposable
{
    private readonly string tempDir;
    private readonly byte[] encryptionKey;
    private readonly AesGcmContentEncryptor contentEncryptor;
    private readonly SqliteVecMemoryStore store;

    public EncryptedStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_enctest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        this.encryptionKey = RandomNumberGenerator.GetBytes(32);
        this.contentEncryptor = new AesGcmContentEncryptor(this.encryptionKey);

        var options = Options.Create(new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            MemoriesSubdirectory = "memories",
            DatabaseFileName = "encrypted.db",
            Ollama = new OllamaOptions { Dimensions = 4 },
        });

        var logger = Substitute.For<ILogger<SqliteVecMemoryStore>>();
        var databaseKey = Convert.ToBase64String(this.encryptionKey);

        this.store = new SqliteVecMemoryStore(options, this.contentEncryptor, logger, databaseKey);
    }

    public void Dispose()
    {
        this.store.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            try { Directory.Delete(this.tempDir, recursive: true); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StoreAndGet_WithEncryption_RoundTrips()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var content = "This is encrypted content.";
        var (chunks, vectors) = CreateTestChunks(memoryId, title: "Encrypted");

        await this.store.StoreMemoryAsync(memoryId, content, chunks, vectors);

        var result = await this.store.GetMemoryAsync(memoryId);

        Assert.NotNull(result);
        Assert.Equal(content, result.Content);
        Assert.Equal("Encrypted", result.Title);
    }

    [Fact]
    public async Task ContentFile_IsActuallyEncrypted()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var content = "This should not appear as plaintext on disk.";
        var (chunks, vectors) = CreateTestChunks(memoryId);

        await this.store.StoreMemoryAsync(memoryId, content, chunks, vectors);

        // Read raw file bytes — they should NOT contain the plaintext
        var filePath = Path.Combine(this.tempDir, "memories", $"{memoryId}.memory.data");
        var rawBytes = await File.ReadAllBytesAsync(filePath);
        var rawText = Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain("This should not appear", rawText);
    }

    [Fact]
    public async Task Search_WithEncryption_ReturnsDecryptedContent()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var content = "Searchable encrypted content.";
        var vector = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var chunks = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = memoryId, ChunkIndex = 0,
                StartOffset = 0, Length = content.Length,
                Title = "Search", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };

        await this.store.StoreMemoryAsync(memoryId, content, chunks, [vector]);

        var queryVector = new float[] { 0.9f, 0.1f, 0.0f, 0.0f };
        var results = await this.store.SearchAsync(queryVector, limit: 5);

        Assert.Single(results);
        Assert.Equal(content, results[0].Content);
    }

    [Fact]
    public async Task Delete_WithEncryption_RemovesContentFile()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);

        await this.store.StoreMemoryAsync(memoryId, "to be deleted", chunks, vectors);

        var filePath = Path.Combine(this.tempDir, "memories", $"{memoryId}.memory.data");
        Assert.True(File.Exists(filePath));

        await this.store.DeleteMemoryAsync(memoryId);

        Assert.False(File.Exists(filePath));
        Assert.Null(await this.store.GetMemoryAsync(memoryId));
    }

    [Fact]
    public async Task ContentFile_CannotBeDecryptedWithWrongKey()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);

        await this.store.StoreMemoryAsync(memoryId, "secret content", chunks, vectors);

        // Try to decrypt with a different key
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var wrongEncryptor = new AesGcmContentEncryptor(wrongKey);

        var filePath = Path.Combine(this.tempDir, "memories", $"{memoryId}.memory.data");
        var rawBytes = await File.ReadAllBytesAsync(filePath);

        Assert.ThrowsAny<CryptographicException>(() => wrongEncryptor.Decrypt(rawBytes));
    }

    private static (List<ChunkRecord> chunks, List<float[]> vectors) CreateTestChunks(
        string memoryId, string? title = null, int dims = 4)
    {
        var now = DateTimeOffset.UtcNow;
        var chunks = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = memoryId,
                ChunkIndex = 0,
                StartOffset = 0,
                Length = 100,
                Title = title,
                Tags = [],
                CreatedAt = now,
                UpdatedAt = now,
            }
        };

        var rng = Random.Shared;
        var vec = new float[dims];
        for (int i = 0; i < dims; i++) vec[i] = (float)rng.NextDouble();

        return (chunks, [vec]);
    }
}
