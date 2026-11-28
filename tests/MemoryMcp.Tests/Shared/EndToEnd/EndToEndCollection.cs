using Xunit;

namespace MemoryMcp.Tests.Shared.EndToEnd;

/// <summary>
/// The end-to-end tests start real server processes and count the ones they started, so they run one at a time and
/// never beside other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EndToEndCollection
{
    public const string Name = "EndToEnd";
}
