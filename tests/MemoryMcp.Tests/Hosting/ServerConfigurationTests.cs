using MemoryMcp.Hosting;
using MemoryMcp.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MemoryMcp.Tests.Hosting;

[Collection("EnvironmentVariables")]
public class ServerConfigurationTests : IDisposable
{
    private const string DataDirectoryVariable = "MemoryMcp__DataDirectory";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory-mcp-config-" + Guid.NewGuid().ToString("N"));
    private readonly string? originalValue = Environment.GetEnvironmentVariable(DataDirectoryVariable);

    public ServerConfigurationTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
        Environment.SetEnvironmentVariable(DataDirectoryVariable, this.dataDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataDirectoryVariable, this.originalValue);
        Directory.Delete(this.dataDirectory, recursive: true);
    }

    [Fact]
    public void Build_ReadsSharedEnabledFromDataDirectorySettings()
    {
        File.WriteAllText(
            Path.Combine(this.dataDirectory, "settings.json"),
            """{"MemoryMcp":{"Shared":{"Enabled":true}}}""");

        var configuration = ServerConfiguration.Build([]);

        var options = configuration.GetSection(SharedOptions.SectionName).Get<SharedOptions>();
        Assert.NotNull(options);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Build_WithoutSettingsFile_SharedIsDisabled()
    {
        var configuration = ServerConfiguration.Build([]);

        var options = configuration.GetSection(SharedOptions.SectionName).Get<SharedOptions>();
        Assert.True(options is null || !options.Enabled);
    }

    [Fact]
    public void DataDirectory_ReturnsFullPathOfConfiguredDirectory()
    {
        var configuration = ServerConfiguration.Build([]);

        Assert.Equal(Path.GetFullPath(this.dataDirectory), ServerConfiguration.DataDirectory(configuration));
    }

    [Fact]
    public void SharedOptions_Defaults()
    {
        var options = new SharedOptions();

        Assert.False(options.Enabled);
        Assert.Equal(1, options.MaxParallelCalls);
    }
}
