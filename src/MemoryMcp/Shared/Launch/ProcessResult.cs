namespace MemoryMcp.Shared.Launch;

/// <summary>How a program run by <see cref="IProcessRunner.RunAsync"/> ended: its exit code and its output (stdout, then stderr).</summary>
public sealed record ProcessResult(int ExitCode, string Output);
