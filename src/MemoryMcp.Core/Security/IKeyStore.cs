namespace MemoryMcp.Core.Security;

/// <summary>
/// Manages the encryption key using the OS-native secret store.
/// On first call, generates a random AES-256 key and persists it.
/// On subsequent calls, retrieves the existing key.
/// </summary>
public interface IKeyStore
{
    /// <summary>
    /// Returns the 32-byte AES-256 encryption key, creating and storing it if it doesn't exist yet.
    /// </summary>
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default);
}
