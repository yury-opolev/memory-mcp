using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using MemoryMcp.Shared.Service;

namespace MemoryMcp.Tests.Shared.Service;

/// <summary>
/// A control pipe that answers every request line with <see cref="Response"/>, as the service's control listener
/// does, and records the commands it received. One that is not answering accepts and reads, but never answers.
/// </summary>
internal sealed class FakeControlListener : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;
    private readonly bool isAnswering;

    public FakeControlListener(string pipeName, ControlResponse response, bool isAnswering = true)
    {
        this.pipeName = pipeName;
        this.Response = response;
        this.isAnswering = isAnswering;
        this.accepting = Task.Run(this.AcceptAsync);
    }

    public ControlResponse Response { get; }

    public ConcurrentQueue<string> Commands { get; } = new();

    public static ControlResponse CreateResponse(int processId)
    {
        return new ControlResponse("9.8.7", processId, 2, true, null, new CallMetricsSnapshot(12, 1, 1.5, 3, 4, 10, 20.25, 30));
    }

    public async ValueTask DisposeAsync()
    {
        await this.stopping.CancelAsync();
        try
        {
            await this.accepting;
        }
        catch (OperationCanceledException)
        {
        }

        this.stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!this.stopping.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                this.pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(this.stopping.Token);
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(this.stopping.Token);
                this.Commands.Enqueue(command ?? string.Empty);
                if (!this.isAnswering)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, this.stopping.Token);
                }

                await pipe.WriteAsync(Encoding.UTF8.GetBytes(this.Response.ToJson() + "\n"), this.stopping.Token);
                await pipe.FlushAsync(this.stopping.Token);
                var buffer = new byte[16];
                while (await pipe.ReadAsync(buffer, this.stopping.Token) > 0)
                {
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
