using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Core.Evaluation;

/// <summary>Deterministic dev/test split and k-fold assignment by query id (~30% dev / 70% test, or k-fold CV).</summary>
public static class EvalSplits
{
    /// <summary>Development split (tuning).</summary>
    public const string Dev = "dev";

    /// <summary>Held-out test split (pre-registered gate run only).</summary>
    public const string Test = "test";

    /// <summary><see cref="Dev"/> for about <paramref name="devShare"/> of ids, otherwise <see cref="Test"/>; stable across runs.</summary>
    public static string Assign(string queryId, int seed = 20261121, double devShare = 0.3) =>
        Unit(queryId, seed) < devShare ? Dev : Test;

    /// <summary>The fold (0 … <paramref name="folds"/>−1) of <paramref name="queryId"/>.</summary>
    public static int Fold(string queryId, int folds, int seed = 20261121) =>
        Math.Min(folds - 1, (int)(Unit(queryId, seed) * folds));

    private static double Unit(string queryId, int seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + queryId));
        return BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue;
    }
}
