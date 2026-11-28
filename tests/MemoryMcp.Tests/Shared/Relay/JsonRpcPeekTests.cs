using System.Text;
using MemoryMcp.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

public sealed class JsonRpcPeekTests
{
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""", "7")]
    [InlineData("""{"jsonrpc":"2.0","method":"tools/call","id":"abc"}""", "\"abc\"")]
    public void Request_GivesMethodAndRawId(string line, string expectedIdRaw)
    {
        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out var method, out var idRaw, out var isResponse);

        Assert.True(isRead);
        Assert.Equal("tools/call", method);
        Assert.Equal(expectedIdRaw, idRaw);
        Assert.False(isResponse);
    }

    [Fact]
    public void Notification_HasNoId()
    {
        var line = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out var method, out var idRaw, out var isResponse);

        Assert.True(isRead);
        Assert.Equal("notifications/initialized", method);
        Assert.Null(idRaw);
        Assert.False(isResponse);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":3,"result":{"tools":[]}}""", "3")]
    [InlineData("""{"jsonrpc":"2.0","id":"x-1","error":{"code":-1,"message":"no"}}""", "\"x-1\"")]
    public void Response_IsResponse(string line, string expectedIdRaw)
    {
        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out var method, out var idRaw, out var isResponse);

        Assert.True(isRead);
        Assert.Null(method);
        Assert.Equal(expectedIdRaw, idRaw);
        Assert.True(isResponse);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"jsonrpc":"2.0","id":1""")]
    [InlineData("""{"id":1} {"id":2}""")]
    [InlineData("42")]
    public void NotJson_ReturnsFalse(string line)
    {
        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out _, out _, out _);

        Assert.False(isRead);
    }

    [Fact]
    public void JsonArrayBatch_ReturnsFalse()
    {
        var line = """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2,"method":"ping"}]""";

        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out _, out _, out _);

        Assert.False(isRead);
    }

    [Fact]
    public void NestedMethodInParams_IsIgnored()
    {
        var line = """{"jsonrpc":"2.0","params":{"method":"inner","id":99,"result":1},"method":"outer","id":5}""";

        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out var method, out var idRaw, out var isResponse);

        Assert.True(isRead);
        Assert.Equal("outer", method);
        Assert.Equal("5", idRaw);
        Assert.False(isResponse);
    }
}
