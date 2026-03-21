using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Security;
using Xunit;

namespace MemoryMcp.Core.Tests.Security;

public class AesGcmContentEncryptorTests
{
    private static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void RoundTrip_ReturnsOriginalContent()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("Hello, encrypted world!");

        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachTime()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("Same input, different nonce");

        var encrypted1 = encryptor.Encrypt(plaintext);
        var encrypted2 = encryptor.Encrypt(plaintext);

        // Different random nonces should produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Encrypt_OutputIsLargerThanInput()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("test");

        var encrypted = encryptor.Encrypt(plaintext);

        // Should include 12-byte nonce + 16-byte tag + ciphertext
        Assert.Equal(plaintext.Length + 12 + 16, encrypted.Length);
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var encryptor1 = new AesGcmContentEncryptor(GenerateKey());
        var encryptor2 = new AesGcmContentEncryptor(GenerateKey());
        var plaintext = Encoding.UTF8.GetBytes("secret data");

        var encrypted = encryptor1.Encrypt(plaintext);

        Assert.ThrowsAny<CryptographicException>(() => encryptor2.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_TamperedData_Throws()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("integrity check");

        var encrypted = encryptor.Encrypt(plaintext);

        // Tamper with the ciphertext portion (after 28-byte header)
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => encryptor.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_TruncatedData_Throws()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);

        // Data shorter than the required header (28 bytes)
        var tooShort = new byte[20];

        Assert.Throws<ArgumentException>(() => encryptor.Decrypt(tooShort));
    }

    [Fact]
    public void Constructor_WrongKeySize_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmContentEncryptor(new byte[16]));
        Assert.Throws<ArgumentException>(() => new AesGcmContentEncryptor(new byte[64]));
    }

    [Fact]
    public void RoundTrip_EmptyContent_Works()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = Array.Empty<byte>();

        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_LargeContent_Works()
    {
        var key = GenerateKey();
        var encryptor = new AesGcmContentEncryptor(key);
        var plaintext = RandomNumberGenerator.GetBytes(100_000);

        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }
}
