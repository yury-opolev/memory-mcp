using System.Runtime.InteropServices;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Tests.Storage;

/// <summary>
/// The one-time conversion of a database written by the SQLCipher engine the standalone server used to
/// link. The fixture (<c>Fixtures/sqlcipher-store</c>) was written by <c>SQLitePCLRaw.bundle_e_sqlcipher</c>
/// 2.1.11 (SQLCipher 4.5.2, SQLite 3.39.2) through <see cref="SqliteVecMemoryStore"/> with the key below:
/// two memories, their content files unencrypted.
/// </summary>
public sealed class SqlCipherDatabaseConverterTests : IDisposable
{
    private const string FirstMemoryId = "11111111-1111-1111-1111-111111111111";

    private static readonly string password = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"memorymcp_convert_{Guid.NewGuid():N}");
    private readonly string databasePath;
    private readonly byte[] originalBytes;

    public SqlCipherDatabaseConverterTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Storage", "Fixtures", "sqlcipher-store");
        Directory.CreateDirectory(Path.Combine(this.directory, "memories"));
        this.databasePath = Path.Combine(this.directory, "memory.db");
        File.Copy(Path.Combine(fixture, "memory.db"), this.databasePath);
        foreach (var file in Directory.GetFiles(Path.Combine(fixture, "memories")))
        {
            File.Copy(file, Path.Combine(this.directory, "memories", Path.GetFileName(file)));
        }

        this.originalBytes = File.ReadAllBytes(this.databasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a handle may still be closing
        }
    }

    private DatabaseConversionOutcome RunConversion(string? withPassword = null) =>
        SqlCipherDatabaseConverter.ConvertIfNeeded(this.databasePath, withPassword ?? password, NullLogger.Instance);

    private string[] FilesBesideTheDatabase() =>
        Directory.GetFiles(this.directory).Select(Path.GetFileName).Where(name => name != "memory.db").Order(StringComparer.Ordinal).ToArray()!;

    private long ChunkCount()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Password = password,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chunks;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void ConvertIfNeeded_NoDatabase_DoesNothing()
    {
        File.Delete(this.databasePath);

        Assert.Equal(DatabaseConversionOutcome.NoDatabase, this.RunConversion());
        Assert.Empty(this.FilesBesideTheDatabase());
        Assert.False(File.Exists(this.databasePath));
    }

    [Fact]
    public void ConvertIfNeeded_SqlCipherDatabase_ConvertsAndKeepsTheOriginalAsBackup()
    {
        Assert.Equal(DatabaseConversionOutcome.Converted, this.RunConversion());

        Assert.Equal(2, this.ChunkCount());
        Assert.Equal(["memory.db" + SqlCipherDatabaseConverter.BackupSuffix], this.FilesBesideTheDatabase());
        Assert.Equal(this.originalBytes, File.ReadAllBytes(this.databasePath + SqlCipherDatabaseConverter.BackupSuffix));
    }

    [Fact]
    public void ConvertIfNeeded_SqlCipherDatabase_ConvertedFileIsStillEncrypted()
    {
        this.RunConversion();

        var header = File.ReadAllBytes(this.databasePath).AsSpan(0, 16);
        Assert.False(header.SequenceEqual("SQLite format 3\0"u8));
    }

    [Fact]
    public void ConvertIfNeeded_AlreadyConverted_DoesNothing()
    {
        this.RunConversion();
        var converted = File.ReadAllBytes(this.databasePath);

        Assert.Equal(DatabaseConversionOutcome.AlreadyCurrent, this.RunConversion());

        Assert.Equal(converted, File.ReadAllBytes(this.databasePath));
        Assert.Equal(["memory.db" + SqlCipherDatabaseConverter.BackupSuffix], this.FilesBesideTheDatabase());
    }

    [Fact]
    public void ConvertIfNeeded_WrongKey_ThrowsAndLeavesTheOriginalUntouched()
    {
        var wrongKey = Convert.ToBase64String(new byte[32]);

        var exception = Assert.Throws<DatabaseConversionException>(() => this.RunConversion(wrongKey));

        Assert.Contains("memory.db", exception.Message, StringComparison.Ordinal);
        Assert.Equal(this.originalBytes, File.ReadAllBytes(this.databasePath));
        Assert.Empty(this.FilesBesideTheDatabase());
    }

    [Fact]
    public void ConvertIfNeeded_PlaintextDatabase_ThrowsAndLeavesItUntouched()
    {
        File.Delete(this.databasePath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = this.databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE chunks (MemoryId TEXT);";
            command.ExecuteNonQuery();
        }

        var plaintext = File.ReadAllBytes(this.databasePath);

        var exception = Assert.Throws<DatabaseConversionException>(() => this.RunConversion());

        Assert.Contains("not encrypted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(plaintext, File.ReadAllBytes(this.databasePath));
        Assert.Empty(this.FilesBesideTheDatabase());
    }

    [Fact]
    public void ConvertIfNeeded_LeftoverOfAnInterruptedConversion_IsDiscarded()
    {
        File.WriteAllBytes(this.databasePath + SqlCipherDatabaseConverter.ConvertingSuffix, [1, 2, 3]);

        Assert.Equal(DatabaseConversionOutcome.Converted, this.RunConversion());

        Assert.Equal(2, this.ChunkCount());
        Assert.Equal(["memory.db" + SqlCipherDatabaseConverter.BackupSuffix], this.FilesBesideTheDatabase());
    }

    [Fact]
    public void ConvertIfNeeded_BackupOfAnEarlierConversionExists_IsNotOverwritten()
    {
        var earlierBackup = this.databasePath + SqlCipherDatabaseConverter.BackupSuffix;
        File.WriteAllBytes(earlierBackup, [9, 9, 9]);

        Assert.Equal(DatabaseConversionOutcome.Converted, this.RunConversion());

        Assert.Equal([9, 9, 9], File.ReadAllBytes(earlierBackup));
        var backups = this.FilesBesideTheDatabase();
        Assert.Equal(2, backups.Length);
        Assert.Contains(backups, name => name != Path.GetFileName(earlierBackup) && File.ReadAllBytes(Path.Combine(this.directory, name)).AsSpan().SequenceEqual(this.originalBytes));
    }

    [Fact]
    public void ConvertIfNeeded_DatabaseOpenInAnotherProcess_ThrowsAndLeavesTheOriginalInPlace()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Only Windows refuses to replace a file that is open.");

        using (new FileStream(this.databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var exception = Assert.Throws<DatabaseConversionException>(() => this.RunConversion());
            Assert.Contains("in use", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(this.originalBytes, File.ReadAllBytes(this.databasePath));
        Assert.Empty(this.FilesBesideTheDatabase());
    }

    [Fact]
    public void ConvertIfNeeded_SqlCipherDatabaseWithAJournalBesideIt_ThrowsAndLeavesBothUntouched()
    {
        File.WriteAllBytes(this.databasePath + "-journal", [1, 2, 3]);

        var exception = Assert.Throws<DatabaseConversionException>(() => this.RunConversion());

        Assert.Contains("unfinished transaction", exception.Message, StringComparison.Ordinal);
        Assert.Equal(this.originalBytes, File.ReadAllBytes(this.databasePath));
        Assert.Equal(["memory.db-journal"], this.FilesBesideTheDatabase());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(this.databasePath + "-journal"));
    }

    [Fact]
    public void ConvertIfNeeded_ConvertedDatabaseWithAJournalBesideIt_IsExaminedOnACopyAndLeftAlone()
    {
        this.RunConversion();
        File.Delete(this.databasePath + SqlCipherDatabaseConverter.BackupSuffix);
        var converted = File.ReadAllBytes(this.databasePath);
        File.WriteAllBytes(this.databasePath + "-journal", [1, 2, 3]);

        Assert.Equal(DatabaseConversionOutcome.AlreadyCurrent, this.RunConversion());

        Assert.Equal(converted, File.ReadAllBytes(this.databasePath));
        Assert.Equal(["memory.db-journal"], this.FilesBesideTheDatabase());
    }

    [Fact]
    public async Task ConvertIfNeeded_ThenTheStoreOpensIt_AndTheKeywordIndexFindsAnIdentifier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        this.RunConversion();

        var options = new MemoryMcpOptions { DataDirectory = this.directory, Ollama = new OllamaOptions { Dimensions = 4 } };
        using var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance, password);
        await store.InitializeAsync(cancellationToken);

        Assert.Null(store.KeywordIndexUnavailableReason);
        var hits = await store.KeywordSearchAsync(KeywordQuery.Build("EN20261008", stemmed: false)!, 5, MemorySearchFilter.None, cancellationToken);
        Assert.Equal([FirstMemoryId], hits.Select(hit => hit.MemoryId));
        Assert.Equal("emulator gotcha", (await store.GetMemoryAsync(FirstMemoryId, cancellationToken))!.Title);
    }
}
