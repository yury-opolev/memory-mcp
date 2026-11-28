namespace MemoryMcp.Storage;

/// <summary>
/// The database could not be converted to the current engine's format. The original file is unchanged.
/// </summary>
public sealed class DatabaseConversionException : Exception
{
    public DatabaseConversionException(string message)
        : base(message)
    {
    }

    public DatabaseConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
