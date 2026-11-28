using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MemoryMcp.Shared.Service;

/// <summary>The client side of the control pipe: sends one command line and reads the one line of JSON it gets back.</summary>
public static class ControlClient
{
    /// <summary>How long opening the control pipe may take before the service counts as not running.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a connected service may take to answer.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Sends <paramref name="command"/>; returns the service's answer, or null when nothing answered.</summary>
    public static async Task<ControlResponse?> SendAsync(string pipeName, string command, CancellationToken ct)
    {
        var reply = await ExchangeAsync(pipeName, command, AnswerTimeout, ct).ConfigureAwait(false);
        return reply.Response;
    }

    /// <summary>
    /// Sends <paramref name="command"/> and tells apart a service that answered, nothing listening
    /// (<see cref="ControlOutcome.NotRunning"/>), and a service that accepted the connection but gave no usable answer
    /// within <paramref name="answerTimeout"/> (<see cref="ControlOutcome.NoAnswer"/>).
    /// </summary>
    public static async Task<ControlReply> ExchangeAsync(string pipeName, string command, TimeSpan answerTimeout, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new ControlReply(ControlOutcome.NotRunning, null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(answerTimeout);
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var response = answer is null ? null : ControlResponse.FromJson(answer);
            return response is null ? new ControlReply(ControlOutcome.NoAnswer, null) : new ControlReply(ControlOutcome.Answered, response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ControlReply(ControlOutcome.NoAnswer, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ControlReply(ControlOutcome.NoAnswer, null);
        }
    }
}
