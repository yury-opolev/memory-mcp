using System.IO.Pipelines;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>One end of an in-memory connection: reads from one pipe and writes to another. Disposing it closes both.</summary>
internal sealed class DuplexPipeStream : Stream
{
    private readonly PipeReader input;
    private readonly PipeWriter output;
    private readonly Stream inputStream;
    private readonly Stream outputStream;
    private readonly TaskCompletionSource writeFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int isDisposed;
    private volatile bool isWriteBroken;

    public DuplexPipeStream(PipeReader input, PipeWriter output)
    {
        this.input = input;
        this.output = output;
        this.inputStream = input.AsStream();
        this.outputStream = output.AsStream();
    }

    public bool IsDisposed => Volatile.Read(ref this.isDisposed) == 1;

    /// <summary>Completes when a write failed after <see cref="BreakWrites"/>.</summary>
    public Task WriteFailed => this.writeFailed.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.inputStream.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return this.inputStream.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.inputStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.outputStream.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.ThrowIfWriteBroken();
        return this.outputStream.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        this.ThrowIfWriteBroken();
        return this.outputStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <summary>Makes every later write fail as on a broken pipe, while reading goes on.</summary>
    public void BreakWrites()
    {
        this.isWriteBroken = true;
    }

    public override void Flush()
    {
        this.outputStream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return this.outputStream.FlushAsync(cancellationToken);
    }

    private void ThrowIfWriteBroken()
    {
        if (this.isWriteBroken)
        {
            this.writeFailed.TrySetResult();
            throw new IOException("Pipe is broken.");
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref this.isDisposed, 1) == 0)
        {
            this.input.Complete();
            this.output.Complete();
        }

        base.Dispose(disposing);
    }
}
