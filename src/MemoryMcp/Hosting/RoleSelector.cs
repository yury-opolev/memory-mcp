namespace MemoryMcp.Hosting;

/// <summary>Chooses the <see cref="ServerRole"/> from the command line arguments.</summary>
public static class RoleSelector
{
    public static ServerRole Select(string[] args, bool sharedEnabled)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return sharedEnabled ? ServerRole.Relay : ServerRole.Standalone;
        }

        return args[0] switch
        {
            "serve" => ServerRole.Service,
            "start" => ServerRole.Start,
            "stop" => ServerRole.Stop,
            "status" => ServerRole.Status,
            _ => throw new ArgumentException($"Unknown command '{args[0]}'. Expected serve, start, stop or status.", nameof(args)),
        };
    }
}
