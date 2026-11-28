namespace MemoryMcp.Shared.Service;

/// <summary>The outcome of one control request and, when it was <see cref="ControlOutcome.Answered"/>, the answer.</summary>
public sealed record ControlReply(ControlOutcome Outcome, ControlResponse? Response);
