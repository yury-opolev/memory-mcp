using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Core.Security;

/// <summary>
/// Key store for macOS (Keychain) and Linux (libsecret/secret-tool).
/// Both platforms provide CLI tools that store secrets in the OS-native secret manager.
/// </summary>
public class CommandLineKeyStore : IKeyStore
{
    private const int KeySize = 32; // AES-256
    private const string ServiceName = "memory-mcp";
    private const string AccountName = "encryption-key";

    private readonly Platform platform;
    private readonly ILogger<CommandLineKeyStore> logger;
    private byte[]? cachedKey;

    public enum Platform { MacOS, Linux }

    public CommandLineKeyStore(Platform platform, ILogger<CommandLineKeyStore> logger)
    {
        this.platform = platform;
        this.logger = logger;
    }

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        if (this.cachedKey is not null)
        {
            return this.cachedKey;
        }

        // Try to retrieve existing key
        var existing = await this.RetrieveKeyAsync(cancellationToken);
        if (existing is not null)
        {
            this.cachedKey = existing;
            this.logger.LogDebug("Retrieved encryption key from OS secret store.");
            return this.cachedKey;
        }

        // Generate and store a new key
        this.cachedKey = RandomNumberGenerator.GetBytes(KeySize);
        var keyBase64 = Convert.ToBase64String(this.cachedKey);
        await this.StoreKeyAsync(keyBase64, cancellationToken);
        this.logger.LogInformation("Generated and stored new encryption key in OS secret store.");
        return this.cachedKey;
    }

    private async Task<byte[]?> RetrieveKeyAsync(CancellationToken cancellationToken)
    {
        var (command, args) = this.platform switch
        {
            Platform.MacOS => ("security", $"find-generic-password -s \"{ServiceName}\" -a \"{AccountName}\" -w"),
            Platform.Linux => ("secret-tool", $"lookup service {ServiceName} account {AccountName}"),
            _ => throw new PlatformNotSupportedException(),
        };

        var (exitCode, output) = await RunCommandAsync(command, args, stdin: null, cancellationToken);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return Convert.FromBase64String(output.Trim());
    }

    private async Task StoreKeyAsync(string keyBase64, CancellationToken cancellationToken)
    {
        var (command, args, stdin) = this.platform switch
        {
            Platform.MacOS => ("security", $"add-generic-password -s \"{ServiceName}\" -a \"{AccountName}\" -w \"{keyBase64}\" -U", (string?)null),
            Platform.Linux => ("secret-tool", $"store --label=\"{ServiceName}\" service {ServiceName} account {AccountName}", keyBase64),
            _ => throw new PlatformNotSupportedException(),
        };

        var (exitCode, output) = await RunCommandAsync(command, args, stdin, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to store encryption key in OS secret store. Command '{command}' exited with code {exitCode}. Output: {output}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string args, string? stdin, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteLineAsync(stdin);
            process.StandardInput.Close();
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, string.IsNullOrEmpty(output) ? error : output);
    }
}
