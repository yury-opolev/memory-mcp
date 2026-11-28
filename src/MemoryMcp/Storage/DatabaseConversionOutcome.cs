namespace MemoryMcp.Storage;

/// <summary>What <see cref="SqlCipherDatabaseConverter.ConvertIfNeeded"/> found and did.</summary>
public enum DatabaseConversionOutcome
{
    /// <summary>There is no database file yet; the store creates it in the current format.</summary>
    NoDatabase,

    /// <summary>The database already opens with the current engine; nothing was changed.</summary>
    AlreadyCurrent,

    /// <summary>A SQLCipher database was converted; the original is kept beside it as a backup.</summary>
    Converted,
}
