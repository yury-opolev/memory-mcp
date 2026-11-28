using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Shared;

/// <summary>Derives pipe and launcher names from a data directory, so every spelling of it maps to one service.</summary>
public static class PipeNames
{
    private const string Prefix = "memory-mcp-";

    public static string Session(string dataDirectory)
    {
        var normalised = Normalise(dataDirectory);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public static string Control(string dataDirectory)
    {
        return Session(dataDirectory) + "-control";
    }

    public static string Hash(string dataDirectory)
    {
        return Session(dataDirectory)[Prefix.Length..][..8];
    }

    private static string Normalise(string dataDirectory)
    {
        var fullPath = Path.GetFullPath(dataDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (fullPath.Length > (root?.Length ?? 0))
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;
    }
}
