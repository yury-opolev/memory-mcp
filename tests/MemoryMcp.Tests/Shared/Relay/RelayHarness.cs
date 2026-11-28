using System.IO.Pipelines;
using MemoryMcp.Shared.Relay;
using MemoryMcp.Tests.Shared.Service;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A running <see cref="RelaySession"/> over in-memory client streams, a fake connector and a manual clock.</summary>
internal sealed class RelayHarness : IAsyncDisposable
{
    private readonly Pipe clientToRelay = new();
    private readonly Pipe relayToClient = new();
    private readonly CancellationTokenSource hangGuard;
    private bool isInputClosed;

    private RelayHarness()
    {
        // Only a guard against a hanging test; no assertion depends on real time passing.
        this.hangGuard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        this.hangGuard.CancelAfter(TimeSpan.FromSeconds(30));
        var session = new RelaySession(
            this.clientToRelay.Reader.AsStream(),
            this.relayToClient.Writer.AsStream(),
            this.Connector,
            this.Logger,
            this.Time);
        this.Run = session.RunAsync(this.Token);
    }

    public FakeServiceConnector Connector { get; } = new();

    public ManualTimerTimeProvider Time { get; } = new();

    public ListLogger<RelaySession> Logger { get; } = new();

    public Task<int> Run { get; }

    public CancellationToken Token => this.hangGuard.Token;

    public static RelayHarness Start()
    {
        return new RelayHarness();
    }

    public Task SendAsync(string line)
    {
        return PipeLines.WriteLineAsync(this.clientToRelay.Writer, line, this.Token);
    }

    /// <summary>The next line the relay wrote to the client, or null after the last one once the session ended.</summary>
    public Task<string?> ReadLineAsync()
    {
        return PipeLines.ReadLineAsync(this.relayToClient.Reader, this.Token);
    }

    public void CloseInput()
    {
        if (!this.isInputClosed)
        {
            this.isInputClosed = true;
            this.clientToRelay.Writer.Complete();
        }
    }

    /// <summary>Waits for the session to end, then ends the client output so reading it returns null after the last line.</summary>
    public async Task<int> WaitForExitAsync()
    {
        var exitCode = await this.Run.WaitAsync(this.Token);
        await this.relayToClient.Writer.CompleteAsync();
        return exitCode;
    }

    /// <summary>
    /// Runs the MCP handshake through the relay on <paramref name="service"/>: initialize with id 1, its response,
    /// then the initialized notification.
    /// </summary>
    public async Task HandshakeAsync(FakeServiceEnd service)
    {
        await this.SendAsync(Lines.Initialize);
        Assert.Equal(Lines.Initialize, await service.ReadLineAsync(this.Token));
        await service.WriteLineAsync(Lines.InitializeResult, this.Token);
        Assert.Equal(Lines.InitializeResult, await this.ReadLineAsync());
        await this.SendAsync(Lines.Initialized);
        Assert.Equal(Lines.Initialized, await service.ReadLineAsync(this.Token));
    }

    public async ValueTask DisposeAsync()
    {
        this.CloseInput();
        try
        {
            await this.Run.WaitAsync(this.Token);
        }
        catch (OperationCanceledException)
        {
        }

        this.hangGuard.Dispose();
    }
}
