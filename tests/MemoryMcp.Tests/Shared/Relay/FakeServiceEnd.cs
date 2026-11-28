using System.IO.Pipelines;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>The service side of one fake connection; <see cref="RelayStream"/> is the side the relay gets.</summary>
internal sealed class FakeServiceEnd
{
    private readonly Pipe relayToService = new();
    private readonly Pipe serviceToRelay = new();

    public FakeServiceEnd()
    {
        this.RelayStream = new DuplexPipeStream(this.serviceToRelay.Reader, this.relayToService.Writer);
    }

    public DuplexPipeStream RelayStream { get; }

    /// <summary>The next line the relay sent, or null once the relay closed the connection.</summary>
    public Task<string?> ReadLineAsync(CancellationToken ct)
    {
        return PipeLines.ReadLineAsync(this.relayToService.Reader, ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        return PipeLines.WriteLineAsync(this.serviceToRelay.Writer, line, ct);
    }

    /// <summary>Makes the relay's writes to this service fail, as when the service process has gone, while it can still read what was sent.</summary>
    public void BreakRelayWrites()
    {
        this.RelayStream.BreakWrites();
    }

    /// <summary>Drops the connection from the service side: the relay reads the end of its input.</summary>
    public void Close()
    {
        this.serviceToRelay.Writer.Complete();
    }
}
