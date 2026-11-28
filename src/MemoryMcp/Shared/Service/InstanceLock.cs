using System.Globalization;
using System.Text;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// Holds <c>service.lock</c> open for writing for the life of the process, so only one service runs per data
/// directory. The file holds the process id and version and stays readable. It is never deleted: a file left
/// behind by a crashed service has no holder and is taken over by the next one.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    // fcntl record locks (Linux) belong to the process, so a second acquire inside one process is caught here.
    private static readonly HashSet<string> heldPaths = new(StringComparer.Ordinal);

    private readonly FileStream stream;
    private readonly string path;
    private int isDisposed;

    private InstanceLock(FileStream stream, string path)
    {
        this.stream = stream;
        this.path = path;
    }

    /// <summary>Takes the lock, or returns null when another live service holds it.</summary>
    public static InstanceLock? TryAcquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.GetFullPath(SharedPaths.ServiceLock(dataDirectory));
        lock (heldPaths)
        {
            if (heldPaths.Contains(path))
            {
                return null;
            }

            var stream = OpenExclusive(path);
            if (stream is null)
            {
                return null;
            }

            try
            {
                var content = Encoding.UTF8.GetBytes(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n" + MemoryServiceHost.Version + "\n");
                stream.SetLength(0);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            heldPaths.Add(path);
            return new InstanceLock(stream, path);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.isDisposed, 1) != 0)
        {
            return;
        }

        lock (heldPaths)
        {
            this.stream.Dispose();
            heldPaths.Remove(this.path);
        }
    }

    private static FileStream? OpenExclusive(string path)
    {
        // Windows: the sharing mode alone keeps a second writer out while readers can still read the file.
        // Linux: FileShare other than None maps to a shared flock, so an fcntl write lock provides exclusion.
        // macOS has no FileStream.Lock, so it takes an exclusive flock (the file is then not readable while held).
        var share = OperatingSystem.IsMacOS() ? FileShare.None : FileShare.Read;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
        }
        catch (IOException)
        {
            return null;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                stream.Lock(0, 1);
            }
            catch (IOException)
            {
                stream.Dispose();
                return null;
            }
        }

        return stream;
    }
}
