using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryMcp.Core.Evaluation;

/// <summary>
/// Pre-registration of an evaluation: <see cref="Register"/> records the SHA-256 of every named file
/// (dataset, analysis plan, ablation plan) once; <see cref="Verify"/> refuses a confirmatory run when a
/// registered file is missing from the record or changed since. Never overwrites a registration.
/// </summary>
public sealed class Preregistration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string registrationPath;
    private readonly IReadOnlyDictionary<string, string> files;

    /// <param name="registrationPath">Where the hashes are written (e.g. <c>preregistration.json</c>).</param>
    /// <param name="files">Registered name → file path.</param>
    public Preregistration(string registrationPath, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationPath);
        ArgumentNullException.ThrowIfNull(files);
        this.registrationPath = registrationPath;
        this.files = files;
    }

    /// <summary>True once <see cref="Register"/> ran.</summary>
    public bool IsRegistered => File.Exists(this.registrationPath);

    /// <summary>Records the hashes. Throws when already registered.</summary>
    public void Register()
    {
        if (this.IsRegistered)
        {
            throw new InvalidOperationException("Already registered. Start a new workspace for a new, separately reported evaluation.");
        }

        File.WriteAllText(this.registrationPath, JsonSerializer.Serialize(this.CurrentHashes(), Json));
    }

    /// <summary>Throws unless the registered hashes match the current files.</summary>
    public void Verify()
    {
        if (!this.IsRegistered)
        {
            throw new InvalidOperationException("Run 'register' before 'holdout': the analysis plan must be fixed before the first held-out run.");
        }

        var registered = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(this.registrationPath), Json) ?? [];
        var changed = this.CurrentHashes()
            .Where(kv => !registered.TryGetValue(kv.Key, out var hash) || !string.Equals(hash, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        if (changed.Count > 0)
        {
            throw new InvalidOperationException($"Pre-registered files changed since 'register': {string.Join(", ", changed)}.");
        }
    }

    /// <summary>SHA-256 (lower-case hex) of every registered file; <c>missing</c> for absent files.</summary>
    public IReadOnlyDictionary<string, string> CurrentHashes() =>
        this.files.ToDictionary(kv => kv.Key, kv => Hash(kv.Value), StringComparer.Ordinal);

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
}
