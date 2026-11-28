using MemoryMcp.Core.Services;

namespace MemoryMcp.Shared.Service;

/// <summary>One independent store (its own database connection) with the services built on it.</summary>
/// <param name="Memory">The memory service over this lane's store.</param>
/// <param name="Search">The memory search over this lane's store.</param>
/// <param name="Owner">Disposing it releases the lane's store and connection.</param>
public sealed record StoreLane(IMemoryService Memory, IHybridMemorySearch Search, IAsyncDisposable Owner);
