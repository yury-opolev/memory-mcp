namespace MemoryMcp.Shared.Launch;

/// <summary>The names the install scripts register the per-OS launchers under, from <see cref="PipeNames.Hash"/>.</summary>
public static class LauncherNames
{
    public static string WindowsTask(string hash)
    {
        return $"MemoryMcp-{hash}";
    }

    public static string SystemdUnit(string hash)
    {
        return $"memory-mcp-{hash}.service";
    }

    public static string LaunchdLabel(string hash)
    {
        return $"io.github.memory-mcp.{hash}";
    }
}
