namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Byte-level inspection of a SQLite database and its journal/WAL side files.
/// Files are opened with full sharing so they can be read while the store's
/// connection is still open (Windows would otherwise refuse the read).
/// </summary>
internal static class DatabaseBytes
{
    public static bool Contains(string databasePath, byte[] needle)
    {
        foreach (var path in new[] { databasePath, databasePath + "-journal", databasePath + "-wal" })
        {
            if (File.Exists(path) && ReadShared(path).AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static byte[] Of(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
