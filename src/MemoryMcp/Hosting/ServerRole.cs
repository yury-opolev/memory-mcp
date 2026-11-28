namespace MemoryMcp.Hosting;

/// <summary>The role this process plays, chosen from the command line and the shared-mode setting.</summary>
public enum ServerRole
{
    Standalone,
    Relay,
    Service,
    Start,
    Stop,
    Status,
}
