using System.Runtime.InteropServices;
using MemoryMcp.Core.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Security;

public class KeyStoreFactoryTests
{
    [Fact]
    public void Create_OnCurrentPlatform_ReturnsAppropriateStore()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var store = KeyStoreFactory.Create(Path.GetTempPath(), loggerFactory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<DpapiKeyStore>(store);
        }
        else
        {
            Assert.IsType<CommandLineKeyStore>(store);
        }
    }
}
