using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Loads the sqlite-vec extension (vec0) vendored under <c>runtimes/&lt;rid&gt;/native/</c>
/// (see <c>scripts/update-sqlite-vec.ps1</c>) and rejects releases older than
/// <see cref="MinimumVersion"/>, the first one whose DELETE wipes the vector bytes.
/// </summary>
internal static class SqliteVecExtension
{
    /// <summary>Oldest sqlite-vec release that physically clears deleted vectors.</summary>
    public static readonly Version MinimumVersion = new(0, 1, 9);

    /// <summary>
    /// Loads vec0 into <paramref name="connection"/> and returns the loaded version (e.g. "v0.1.9").
    /// </summary>
    public static string Load(SqliteConnection connection)
    {
        connection.LoadExtension(ResolvePath(AppContext.BaseDirectory));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vec_version()";
        var version = (string)cmd.ExecuteScalar()!;
        EnsureSupported(version);
        return version;
    }

    /// <summary>Full path of the vendored vec0 library for the current OS and CPU.</summary>
    public static string ResolvePath(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "runtimes", GetRuntimeIdentifier(), "native", GetFileName());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native library not found at '{path}'. It is vendored in MemoryMcp.Core/runtimes and copied on build.",
                path);
        }

        return path;
    }

    /// <summary>True when a process running <paramref name="vecVersion"/> physically clears the vector bytes it deletes.</summary>
    public static bool WipesDeletedVectors(string vecVersion)
    {
        var core = vecVersion.TrimStart('v').Split('-', 2)[0];
        return Version.TryParse(core, out var parsed) && parsed >= MinimumVersion;
    }

    /// <summary>Throws when <paramref name="vecVersion"/> (as returned by <c>vec_version()</c>) is too old.</summary>
    public static void EnsureSupported(string vecVersion)
    {
        var core = vecVersion.TrimStart('v').Split('-', 2)[0];
        if (!Version.TryParse(core, out var parsed) || parsed < MinimumVersion)
        {
            throw new InvalidOperationException(
                $"sqlite-vec {vecVersion} is not supported: {MinimumVersion} or newer is required so that deletes erase vector bytes.");
        }
    }

    private static string GetRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("sqlite-vec is vendored for Windows, Linux and macOS only.");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException($"sqlite-vec is not vendored for {other}."),
        };
        return $"{os}-{arch}";
    }

    private static string GetFileName() =>
        OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so";
}
