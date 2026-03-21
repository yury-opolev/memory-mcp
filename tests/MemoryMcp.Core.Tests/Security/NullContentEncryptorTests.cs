using MemoryMcp.Core.Security;
using Xunit;

namespace MemoryMcp.Core.Tests.Security;

public class NullContentEncryptorTests
{
    [Fact]
    public void Encrypt_ReturnsSameBytes()
    {
        var encryptor = new NullContentEncryptor();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var result = encryptor.Encrypt(data);

        Assert.Same(data, result);
    }

    [Fact]
    public void Decrypt_ReturnsSameBytes()
    {
        var encryptor = new NullContentEncryptor();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var result = encryptor.Decrypt(data);

        Assert.Same(data, result);
    }
}
