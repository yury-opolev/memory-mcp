namespace MemoryMcp.Shared;

public static class SharedPaths
{
    public static string ServiceLock(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "service.lock");
    }

    public static string StopMarker(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "service.stopped");
    }

    public static string ServicePathFile(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "service-path.txt");
    }

    public static string LogDirectory(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "logs");
    }
}
