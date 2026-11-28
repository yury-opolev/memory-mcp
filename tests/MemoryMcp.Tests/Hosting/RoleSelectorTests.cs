using MemoryMcp.Hosting;
using Xunit;

namespace MemoryMcp.Tests.Hosting;

public class RoleSelectorTests
{
    [Theory]
    [InlineData(new string[0], false, ServerRole.Standalone)]
    [InlineData(new string[0], true, ServerRole.Relay)]
    [InlineData(new[] { "serve" }, false, ServerRole.Service)]
    [InlineData(new[] { "serve" }, true, ServerRole.Service)]
    [InlineData(new[] { "start" }, true, ServerRole.Start)]
    [InlineData(new[] { "stop" }, true, ServerRole.Stop)]
    [InlineData(new[] { "status" }, false, ServerRole.Status)]
    [InlineData(new[] { "--MemoryMcp:DataDirectory=x" }, true, ServerRole.Relay)]
    public void Select_ReturnsRole(string[] args, bool shared, ServerRole expected)
    {
        Assert.Equal(expected, RoleSelector.Select(args, shared));
    }

    [Fact]
    public void Select_UnknownCommand_Throws()
    {
        Assert.Throws<ArgumentException>(() => RoleSelector.Select(["bogus"], false));
    }
}
