using System.Globalization;

namespace MemoryMcp.Shared.Service;

/// <summary>The file that says the service was stopped on purpose and must not be started until <c>start</c>.</summary>
public static class StopMarker
{
    public static bool Exists(string dataDirectory)
    {
        return File.Exists(SharedPaths.StopMarker(dataDirectory));
    }

    public static void Set(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(SharedPaths.StopMarker(dataDirectory), DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
    }

    public static void Clear(string dataDirectory)
    {
        File.Delete(SharedPaths.StopMarker(dataDirectory));
    }
}
