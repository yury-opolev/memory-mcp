using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MemoryMcp.Tests.Shared.EndToEnd;

/// <summary>
/// The built <c>MemoryMcp</c> executable started with no arguments for a data directory, as an MCP client starts it,
/// speaking newline-delimited JSON-RPC over its stdio. With shared mode on it is a relay; with it off, the standalone
/// server. Its standard error is kept for the failure messages.
/// </summary>
internal sealed class ServerProcess : IAsyncDisposable
{
    /// <summary>How long a response may take: it covers starting the service and the relay's reconnect timeout.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(75);

    private const string SettingPrefix = "MemoryMcp__";
    private const string DataDirectoryVariable = "MemoryMcp__DataDirectory";

    private static readonly TimeSpan exitTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding utf8WithoutMark = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process process;
    private readonly Channel<string> outputLines = Channel.CreateUnbounded<string>();
    private readonly StringBuilder errorOutput = new();
    private readonly List<string> nonJsonOutputLines = [];
    private readonly Task outputReader;
    private int lastRequestId;

    private ServerProcess(Process process)
    {
        this.process = process;
        this.process.ErrorDataReceived += this.OnErrorData;
        this.process.BeginErrorReadLine();
        this.outputReader = this.ReadOutputAsync();
    }

    /// <summary>The executable beside the test assembly (the server project is referenced, so it is copied there).</summary>
    public static string ExecutablePath { get; } =
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MemoryMcp.exe" : "MemoryMcp");

    public int ProcessId => this.process.Id;

    /// <summary>The lines of standard output read so far that were not JSON-RPC messages.</summary>
    public IReadOnlyList<string> NonJsonOutputLines => this.nonJsonOutputLines;

    /// <summary>What the process wrote to standard error so far.</summary>
    public string ErrorOutput
    {
        get
        {
            lock (this.errorOutput)
            {
                return this.errorOutput.ToString();
            }
        }
    }

    public static ServerProcess Start(string dataDirectory)
    {
        var startInfo = CreateStartInfo(dataDirectory, []);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{ExecutablePath} did not start.");
        return new ServerProcess(process);
    }

    /// <summary>
    /// The start info for the executable with <paramref name="arguments"/>: redirected UTF-8 stdio, and of this
    /// process's <c>MemoryMcp__*</c> variables only the data directory, so nothing of the machine's own settings leaks in.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string dataDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8WithoutMark,
            StandardOutputEncoding = utf8WithoutMark,
            StandardErrorEncoding = utf8WithoutMark,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var inherited = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.StartsWith(SettingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                inherited.Add(name);
            }
        }

        foreach (var name in inherited)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment[DataDirectoryVariable] = dataDirectory;
        return startInfo;
    }

    /// <summary>The text of a <c>tools/call</c> result's content blocks.</summary>
    public static string ToolText(JsonObject response)
    {
        var content = response["result"]?["content"]?.AsArray() ?? throw new InvalidOperationException($"Not a tool result: {response.ToJsonString()}");
        return string.Concat(content.Select(block => block?["text"]?.GetValue<string>() ?? string.Empty));
    }

    /// <summary>The message of a JSON-RPC error response, or null when it is a result.</summary>
    public static string? ErrorMessage(JsonObject response)
    {
        return response["error"]?["message"]?.GetValue<string>();
    }

    /// <summary>The handshake: <c>initialize</c>, which must succeed, then <c>notifications/initialized</c>.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "memory-mcp-end-to-end-tests", ["version"] = "1.0" },
        };
        var response = await this.RequestAsync("initialize", parameters, ct);
        if (response["result"] is null)
        {
            throw new InvalidOperationException($"initialize failed: {response.ToJsonString()}\n{this.ErrorOutput}");
        }

        await this.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    /// <summary>The names of the listed tools, sorted.</summary>
    public async Task<string[]> ListToolNamesAsync(CancellationToken ct)
    {
        var response = await this.RequestAsync("tools/list", new JsonObject(), ct);
        var tools = response["result"]?["tools"]?.AsArray()
            ?? throw new InvalidOperationException($"tools/list failed: {response.ToJsonString()}\n{this.ErrorOutput}");
        return [.. tools.Select(tool => tool!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    public Task<JsonObject> CallToolAsync(string toolName, JsonObject arguments, CancellationToken ct)
    {
        return this.RequestAsync("tools/call", new JsonObject { ["name"] = toolName, ["arguments"] = arguments }, ct);
    }

    /// <summary>Sends a request and returns the response with its id, skipping notifications and other lines.</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++this.lastRequestId;
        await this.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            while (true)
            {
                if (!await this.outputLines.Reader.WaitToReadAsync(timeout.Token))
                {
                    throw new InvalidOperationException(
                        $"The server closed its output before answering {method} (exit code {this.ExitCodeText()}).\n{this.ErrorOutput}");
                }

                while (this.outputLines.Reader.TryRead(out var line))
                {
                    if (ParseMessage(line) is not { } message)
                    {
                        // The standalone server logs to its stdout (as it always has); MCP clients skip such lines.
                        this.nonJsonOutputLines.Add(line);
                        continue;
                    }

                    if (message["id"] is JsonValue idValue
                        && idValue.TryGetValue<int>(out var responseId)
                        && responseId == id
                        && message["method"] is null)
                    {
                        return message;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No response to {method} within {ResponseTimeout.TotalSeconds} seconds.\n{this.ErrorOutput}");
        }
    }

    /// <summary>Closes the input (a relay or standalone server then exits on its own); kills the process if it does not.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            this.process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process has exited already.
        }

        using (var timeout = new CancellationTokenSource(exitTimeout))
        {
            try
            {
                await this.process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Only this process: a detached service it started is not part of it and is stopped separately.
                this.process.Kill(entireProcessTree: false);
                await this.process.WaitForExitAsync(CancellationToken.None);
            }
        }

        await this.outputReader;
        this.process.Dispose();
    }

    private static JsonObject? ParseMessage(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteLineAsync(JsonObject message)
    {
        // A bare newline ends the line on every OS, as MCP's stdio transport defines it.
        await this.process.StandardInput.WriteAsync(message.ToJsonString() + "\n");
        await this.process.StandardInput.FlushAsync();
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await this.process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    this.outputLines.Writer.TryWrite(line);
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke when the process was killed; the output ends there.
        }
        finally
        {
            this.outputLines.Writer.TryComplete();
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        lock (this.errorOutput)
        {
            this.errorOutput.AppendLine(e.Data);
        }
    }

    private string ExitCodeText()
    {
        return this.process.WaitForExit(TimeSpan.FromSeconds(5)) ? this.process.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running";
    }
}
