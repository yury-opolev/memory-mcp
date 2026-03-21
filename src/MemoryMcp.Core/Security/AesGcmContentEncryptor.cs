using System.Security.Cryptography;

namespace MemoryMcp.Core.Security;

/// <summary>
/// Encrypts content using AES-256-GCM.
/// Wire format: [12-byte nonce][16-byte tag][ciphertext]
/// Thread-safe: each call generates a fresh random nonce.
/// </summary>
public class AesGcmContentEncryptor : IContentEncryptor
{
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;   // AES-GCM standard tag size
    private const int HeaderSize = NonceSize + TagSize;
    private const int KeySize = 32;   // AES-256

    private readonly byte[] key;

    public AesGcmContentEncryptor(byte[] key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be exactly {KeySize} bytes for AES-256.", nameof(key));
        }

        this.key = (byte[])key.Clone();
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(this.key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Pack as [nonce][tag][ciphertext]
        var result = new byte[HeaderSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, HeaderSize);
        return result;
    }

    public byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < HeaderSize)
        {
            throw new ArgumentException("Encrypted data is too short to contain a valid header.", nameof(blob));
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ciphertext = blob.AsSpan(HeaderSize);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(this.key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
