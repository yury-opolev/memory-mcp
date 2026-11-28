using Xunit;

namespace MemoryMcp.Tests;

/// <summary>
/// Tests that change process-wide state (environment variables, the standard handles) run one at a time and never
/// beside other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection
{
    public const string Name = "EnvironmentVariables";
}
