using System.Text.Json;
using MemoryMcp.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>JSON-RPC lines the relay tests send and expect.</summary>
internal static class Lines
{
    public const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    public const string InitializeResult =
        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"memory-mcp","version":"1"}}}""";

    public const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    public const string ReplayResult =
        """{"jsonrpc":"2.0","id":"relay-replay","result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"memory-mcp","version":"1"}}}""";

    public static string ToolCall(string idRaw)
    {
        return """{"jsonrpc":"2.0","id":""" + idRaw + ""","method":"tools/call","params":{"name":"search_memory","arguments":{"query":"secret words"}}}""";
    }

    public static string ToolResult(string idRaw)
    {
        return """{"jsonrpc":"2.0","id":""" + idRaw + ""","result":{"content":[{"type":"text","text":"found"}]}}""";
    }

    /// <summary>Asserts that <paramref name="line"/> is a relay error response for <paramref name="idRaw"/>.</summary>
    public static void AssertError(string? line, string idRaw, string message)
    {
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(idRaw, root.GetProperty("id").GetRawText());
        Assert.Equal(RelayMessages.ErrorCode, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(message, root.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(3, root.EnumerateObject().Count());
    }
}
