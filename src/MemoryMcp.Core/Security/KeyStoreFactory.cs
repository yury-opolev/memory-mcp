using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Core.Security;

/// <summary>
/// Creates the appropriate <see cref="IKeyStore"/> for the current OS platform.
/// </summary>
public static class KeyStoreFactory
{
    public static IKeyStore Create(string dataDirectory, ILoggerFactory loggerFactory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DpapiKeyStore(dataDirectory, loggerFactory.CreateLogger<DpapiKeyStore>());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new CommandLineKeyStore(
                CommandLineKeyStore.Platform.MacOS,
                loggerFactory.CreateLogger<CommandLineKeyStore>());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new CommandLineKeyStore(
                CommandLineKeyStore.Platform.Linux,
                loggerFactory.CreateLogger<CommandLineKeyStore>());
        }

        throw new PlatformNotSupportedException(
            "Encryption is not supported on this platform. Disable encryption in configuration.");
    }
}
