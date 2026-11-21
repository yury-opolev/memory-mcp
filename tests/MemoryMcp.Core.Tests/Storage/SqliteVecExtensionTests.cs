using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

public class SqliteVecExtensionTests
{
    [Fact]
    public void Load_VendoredLibrary_IsAtLeastMinimumVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var version = SqliteVecExtension.Load(connection);

        var parsed = Version.Parse(version.TrimStart('v').Split('-', 2)[0]);
        Assert.True(parsed >= SqliteVecExtension.MinimumVersion, $"loaded sqlite-vec {version}");
    }

    [Theory]
    [InlineData("v0.1.7-alpha.2.1")]
    [InlineData("v0.1.8")]
    [InlineData("garbage")]
    public void EnsureSupported_OlderOrUnparsableVersion_Throws(string version)
    {
        Assert.Throws<InvalidOperationException>(() => SqliteVecExtension.EnsureSupported(version));
    }

    [Theory]
    [InlineData("v0.1.9")]
    [InlineData("v0.1.10-alpha.4")]
    [InlineData("v0.2.0")]
    public void EnsureSupported_SupportedVersion_DoesNotThrow(string version)
    {
        SqliteVecExtension.EnsureSupported(version);
    }
}
