namespace MemoryMcp.Tests.Shared.Launch;

/// <summary>One program the <see cref="RecordingProcessRunner"/> was asked to run.</summary>
internal sealed record RecordedProcess(string FileName, IReadOnlyList<string> Arguments, bool IsDetached);
