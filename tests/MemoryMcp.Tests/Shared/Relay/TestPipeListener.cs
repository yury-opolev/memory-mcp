using System.IO.Pipes;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A current-user-only pipe server that accepts one connection, standing in for the memory service.</summary>
internal sealed class TestPipeListener : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;

    private TestPipeListener(string pipeName)
    {
        this.pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        this.Connected = this.pipe.WaitForConnectionAsync();
    }

    /// <summary>Completes when a client has connected.</summary>
    public Task Connected { get; }

    public NamedPipeServerStream Pipe => this.pipe;

    public static TestPipeListener Start(string pipeName)
    {
        return new TestPipeListener(pipeName);
    }

    public async ValueTask DisposeAsync()
    {
        await this.pipe.DisposeAsync();
        try
        {
            await this.Connected;
        }
        catch (Exception)
        {
            // Disposed before a client connected.
        }
    }
}
