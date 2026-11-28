namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>
/// A connection whose reads ignore cancellation and end only when the stream is disposed, as some pipe and console
/// streams behave. Writes are accepted and discarded.
/// </summary>
internal sealed class CancellationIgnoringStream : Stream
{
    private readonly TaskCompletionSource<int> disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when something was written, so the relay has taken the connection.</summary>
    public Task Written => this.written.Task;

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
        return this.disposed.Task.GetAwaiter().GetResult();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return new ValueTask<int>(this.disposed.Task);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.disposed.Task;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.written.TrySetResult();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.written.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        this.written.TrySetResult();
        return Task.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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
        // A read pending on a disposed stream ends, as the end of the stream.
        this.disposed.TrySetResult(0);
        base.Dispose(disposing);
    }
}
