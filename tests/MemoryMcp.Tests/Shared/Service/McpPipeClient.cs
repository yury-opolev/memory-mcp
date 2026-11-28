using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemoryMcp.Tests.Shared.Service;

/// <summary>An MCP client over a current-user-only named pipe, as a relay would open it.</summary>
internal sealed class McpPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;

    private McpPipeClient(NamedPipeClientStream pipe, McpClient client)
    {
        this.pipe = pipe;
        this.Client = client;
    }

    public McpClient Client { get; }

    public static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(10_000, ct);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public static async Task<McpPipeClient> ConnectAsync(string pipeName, CancellationToken ct)
    {
        var pipe = await ConnectPipeAsync(pipeName, ct);
        try
        {
            var transport = new StreamClientTransport(pipe, pipe, NullLoggerFactory.Instance);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            return new McpPipeClient(pipe, client);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public static string Text(CallToolResult result)
    {
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    public async ValueTask DisposeAsync()
    {
        await this.Client.DisposeAsync();
        await this.pipe.DisposeAsync();
    }
}
