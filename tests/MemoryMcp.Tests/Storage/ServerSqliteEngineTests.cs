using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Tests.Storage;

/// <summary>
/// The standalone server links SQLite3 Multiple Ciphers, so the engine this test project runs on (it
/// references the server) can host the FTS5 <c>contentless_delete</c> keyword index. The library's own
/// tests still run on the older SQLCipher bundle, where the index reports itself unavailable.
/// </summary>
public sealed class ServerSqliteEngineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"memorymcp_engine_{Guid.NewGuid():N}");

    public ServerSqliteEngineTests()
    {
        Directory.CreateDirectory(this.directory);
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

    [Fact]
    public void Engine_IsSqlite343OrLater()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var version = Version.Parse((string)command.ExecuteScalar()!);

        Assert.True(version >= new Version(3, 43), $"SQLite {version} cannot host the keyword index.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_OnTheServerEngine_HasTheKeywordIndex(bool encrypted)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = new MemoryMcpOptions { DataDirectory = this.directory, Ollama = new OllamaOptions { Dimensions = 4 } };
        var databaseKey = encrypted ? Convert.ToBase64String(new byte[32]) : null;

        using var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance, databaseKey);
        await store.InitializeAsync(cancellationToken);

        Assert.Null(store.KeywordIndexUnavailableReason);
    }
}
