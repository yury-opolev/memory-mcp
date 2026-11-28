using MemoryMcp.Shared.Launch;

namespace MemoryMcp.Tests.Shared.Launch;

/// <summary>Records the programs it is asked to run and answers each with <see cref="ExitCode"/>; it runs nothing.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly List<RecordedProcess> processes = [];

    public int ExitCode { get; set; }

    public IReadOnlyList<RecordedProcess> Processes => this.processes;

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        this.processes.Add(new RecordedProcess(fileName, [.. arguments], IsDetached: false, RemovedVariables: []));
        return Task.FromResult(new ProcessResult(this.ExitCode, string.Empty));
    }

    public void StartDetached(string fileName, IReadOnlyList<string> arguments, IReadOnlyList<string> removedVariables)
    {
        this.processes.Add(new RecordedProcess(fileName, [.. arguments], IsDetached: true, [.. removedVariables]));
    }
}
