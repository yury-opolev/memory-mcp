namespace MemoryMcp.Core.Security;

/// <summary>
/// Encrypts and decrypts byte content.
/// Implementations must be thread-safe.
/// </summary>
public interface IContentEncryptor
{
    /// <summary>
    /// Encrypts plaintext bytes. The returned blob is self-contained
    /// (includes nonce/tag) and can be decrypted only with the same key.
    /// </summary>
    byte[] Encrypt(byte[] plaintext);

    /// <summary>
    /// Decrypts a blob previously produced by <see cref="Encrypt"/>.
    /// </summary>
    byte[] Decrypt(byte[] ciphertext);
}
