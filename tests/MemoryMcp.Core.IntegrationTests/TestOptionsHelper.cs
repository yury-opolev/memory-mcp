using MemoryMcp.Core.Configuration;

namespace MemoryMcp.Core.IntegrationTests;

/// <summary>
/// Creates MemoryMcpOptions with optional environment variable overrides.
/// This allows running integration tests against different models without changing code:
///
///   set MEMORYMCP_OLLAMA_MODEL=bge-m3
///   set MEMORYMCP_OLLAMA_DIMENSIONS=1024
///   dotnet test tests/MemoryMcp.Core.IntegrationTests
///
/// If the environment variables are not set, the defaults from MemoryMcpOptions are used.
/// </summary>
internal static class TestOptionsHelper
{
    public static MemoryMcpOptions CreateOptions(string? dataDirectory = null)
    {
        var options = new MemoryMcpOptions();

        if (dataDirectory != null)
        {
            options.DataDirectory = dataDirectory;
        }

        var model = Environment.GetEnvironmentVariable("MEMORYMCP_OLLAMA_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
        {
            options.Ollama.Model = model;
        }

        var dimensionsStr = Environment.GetEnvironmentVariable("MEMORYMCP_OLLAMA_DIMENSIONS");
        if (!string.IsNullOrWhiteSpace(dimensionsStr) && int.TryParse(dimensionsStr, out var dimensions))
        {
            options.Ollama.Dimensions = dimensions;
        }

        var endpoint = Environment.GetEnvironmentVariable("MEMORYMCP_OLLAMA_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Ollama.Endpoint = endpoint;
        }

        return options;
    }
}
