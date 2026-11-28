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

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("-25", "-2.50e1")]
    [InlineData("\"abc\"", "\"a\\" + "u0062c\"")]
    [InlineData("\"x/y\"", "\"x\\/y\"")]
    public void IdKey_IsTheSameForEveryEncodingOfOneId(string firstIdRaw, string secondIdRaw)
    {
        Assert.True(JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(Line(firstIdRaw)), out _, out var firstRaw, out var firstKey, out _));
        Assert.True(JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(Line(secondIdRaw)), out _, out var secondRaw, out var secondKey, out _));

        Assert.Equal(firstIdRaw, firstRaw);
        Assert.Equal(secondIdRaw, secondRaw);
        Assert.NotNull(firstKey);
        Assert.Equal(firstKey, secondKey);
    }

    [Theory]
    [InlineData("1", "\"1\"")]
    [InlineData("1", "2")]
    [InlineData("\"a\"", "\"A\"")]
    public void IdKey_TellsDifferentIdsApart(string firstIdRaw, string secondIdRaw)
    {
        JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(Line(firstIdRaw)), out _, out _, out var firstKey, out _);
        JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(Line(secondIdRaw)), out _, out _, out var secondKey, out _);

        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public void DeeplyNestedParams_AreStillRead()
    {
        var depth = 500;
        var nested = new string('[', depth) + new string(']', depth);
        var line = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"deep":""" + nested + "}}";

        var isRead = JsonRpcPeek.TryRead(Encoding.UTF8.GetBytes(line), out var method, out var idRaw, out var idKey, out var isResponse);

        Assert.True(isRead);
        Assert.Equal("tools/call", method);
        Assert.Equal("5", idRaw);
        Assert.NotNull(idKey);
        Assert.False(isResponse);
    }

    private static string Line(string idRaw)
    {
        return """{"jsonrpc":"2.0","id":""" + idRaw + ""","result":{}}""";
    }
}
