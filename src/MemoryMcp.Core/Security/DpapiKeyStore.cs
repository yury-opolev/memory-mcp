using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Core.Security;

/// <summary>
/// Windows key store using DPAPI (Data Protection API).
/// DPAPI encrypts the key file so only the current Windows user can decrypt it.
/// The encrypted key is stored as a file at {dataDirectory}/encryption.key.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class DpapiKeyStore : IKeyStore
{
    private const int KeySize = 32; // AES-256
    private const string KeyFileName = "encryption.key";

    private readonly string keyFilePath;
    private readonly ILogger<DpapiKeyStore> logger;
    private byte[]? cachedKey;

    public DpapiKeyStore(string dataDirectory, ILogger<DpapiKeyStore> logger)
    {
        this.keyFilePath = Path.Combine(dataDirectory, KeyFileName);
        this.logger = logger;
    }

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        if (this.cachedKey is not null)
        {
            return Task.FromResult(this.cachedKey);
        }

        if (File.Exists(this.keyFilePath))
        {
            var protectedBytes = File.ReadAllBytes(this.keyFilePath);
            this.cachedKey = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            this.logger.LogDebug("Loaded encryption key from {KeyFile}.", this.keyFilePath);
        }
        else
        {
            this.cachedKey = RandomNumberGenerator.GetBytes(KeySize);
            var protectedBytes = ProtectedData.Protect(this.cachedKey, null, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(this.keyFilePath)!);
            File.WriteAllBytes(this.keyFilePath, protectedBytes);
            this.logger.LogInformation("Generated and stored new encryption key at {KeyFile}.", this.keyFilePath);
        }

        return Task.FromResult(this.cachedKey);
    }
}
