namespace MemoryMcp.Core.Security;

/// <summary>
/// No-op encryptor used when encryption is disabled. Returns data unchanged.
/// </summary>
public class NullContentEncryptor : IContentEncryptor
{
    public byte[] Encrypt(byte[] plaintext) => plaintext;

    public byte[] Decrypt(byte[] ciphertext) => ciphertext;
}
