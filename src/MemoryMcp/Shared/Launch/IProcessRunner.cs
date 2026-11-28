namespace MemoryMcp.Shared.Launch;

/// <summary>Runs other programs. Arguments are always passed as a list, never joined into a shell command line.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> to its end. A program that cannot be started is a non-zero exit, not an exception.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>
    /// Starts <paramref name="fileName"/> without waiting for it and without sharing this process's standard streams,
    /// with this process's environment minus <paramref name="removedVariables"/>.
    /// </summary>
    void StartDetached(string fileName, IReadOnlyList<string> arguments, IReadOnlyList<string> removedVariables);
}
