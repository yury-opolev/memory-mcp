namespace MemoryMcp.Core.Services;

/// <summary>
/// An input did not fit the embedder's context window (<c>num_ctx</c>). Requests are sent with
/// <c>truncate=false</c>, so this is raised instead of embedding a silently truncated text.
/// </summary>
public sealed class EmbeddingInputTooLongException : InvalidOperationException
{
    public EmbeddingInputTooLongException()
    {
    }

    public EmbeddingInputTooLongException(string message)
        : base(message)
    {
    }

    public EmbeddingInputTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
