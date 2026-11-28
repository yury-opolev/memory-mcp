using System.Collections;
using MemoryMcp.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

public sealed class ClientEnvironmentCheckTests
{
    [Fact]
    public void ReportsOllamaSettings_NotDataDirectoryOrSharedSettings()
    {
        var environment = new Hashtable
        {
            ["MemoryMcp__DataDirectory"] = @"C:\data",
            ["MemoryMcp__Shared__Enabled"] = "true",
            ["MemoryMcp__Shared__MaxParallelCalls"] = "2",
            ["MemoryMcp__Ollama__Endpoint"] = "http://localhost:11434",
            ["MemoryMcp__Ollama__Model"] = "qwen3-embedding:0.6b",
            ["PATH"] = @"C:\Windows",
            ["MemoryMcpOther"] = "unrelated",
        };

        var ignored = ClientEnvironmentCheck.IgnoredSettings(environment);

        Assert.Equal(["MemoryMcp__Ollama__Endpoint", "MemoryMcp__Ollama__Model"], ignored);
    }

    [Fact]
    public void MatchesNamesWithoutRegardToCase()
    {
        var environment = new Hashtable
        {
            ["MEMORYMCP__DATADIRECTORY"] = "/data",
            ["memorymcp__shared__enabled"] = "true",
            ["MEMORYMCP__ENCRYPTION__ENABLED"] = "false",
        };

        var ignored = ClientEnvironmentCheck.IgnoredSettings(environment);

        Assert.Equal(["MEMORYMCP__ENCRYPTION__ENABLED"], ignored);
    }

    [Fact]
    public void NoMemoryMcpSettings_ReportsNothing()
    {
        Assert.Empty(ClientEnvironmentCheck.IgnoredSettings(new Hashtable { ["HOME"] = "/home/user" }));
    }
}
