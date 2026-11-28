namespace MemoryMcp.Shared.Launch;

/// <summary>Finds the executable to run as the service.</summary>
public static class ServiceExecutable
{
    /// <summary>
    /// The path in the data directory's <c>service-path.txt</c> (written by the install script) when the file and
    /// the path it names exist; else this process's own executable.
    /// </summary>
    public static string Resolve(string dataDirectory)
    {
        var pathFile = SharedPaths.ServicePathFile(dataDirectory);
        if (File.Exists(pathFile))
        {
            var servicePath = File.ReadAllText(pathFile).Trim();
            if (servicePath.Length > 0 && File.Exists(servicePath))
            {
                return servicePath;
            }
        }

        return Environment.ProcessPath ?? throw new InvalidOperationException("The path of this process's executable is not known.");
    }
}
