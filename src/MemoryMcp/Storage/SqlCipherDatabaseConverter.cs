using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Storage;

/// <summary>
/// One-time conversion of an encrypted memory database written by the SQLCipher engine
/// (<c>SQLitePCLRaw.bundle_e_sqlcipher</c>) that the standalone server linked before it moved to SQLite3
/// Multiple Ciphers. The new engine reads the old file in its SQLCipher compatibility mode
/// (<c>cipher=sqlcipher</c>, <c>legacy=4</c>) and writes a copy in its own default format with
/// <c>VACUUM INTO</c>, so the pages reach the disk encrypted and the original is only ever read. The copy
/// is verified (integrity check, row count of every table) before it replaces the original, and the
/// original stays beside it as <c>&lt;database&gt;.sqlcipher-backup</c>, still encrypted with the same key.
/// A database that already opens with the current engine is left alone, so calling this on every start
/// is safe. Any failure throws <see cref="DatabaseConversionException"/> and leaves the original file
/// in place and unchanged.
/// </summary>
public static class SqlCipherDatabaseConverter
{
    /// <summary>Suffix of the original SQLCipher file kept after a conversion.</summary>
    public const string BackupSuffix = ".sqlcipher-backup";

    /// <summary>Suffix of the copy being written; a leftover from an interrupted run is discarded.</summary>
    public const string ConvertingSuffix = ".converting";

    private const string LockSuffix = ".convert-lock";
    private const string ProbeSuffix = ".convert-probe";
    private const int NotADatabase = 26;

    private static readonly TimeSpan lockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan lockRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly string[] sidecarSuffixes = ["-journal", "-wal", "-shm"];

    /// <summary>
    /// Converts <paramref name="databasePath"/> when it is a SQLCipher database that opens with
    /// <paramref name="password"/>; does nothing when there is no database or it is already current.
    /// </summary>
    /// <param name="databasePath">The memory database file.</param>
    /// <param name="password">The passphrase the store opens the database with.</param>
    /// <param name="logger">Receives one message when a conversion happened.</param>
    /// <exception cref="DatabaseConversionException">The file cannot be read with the key, is not encrypted, is in use, or the copy failed verification.</exception>
    public static DatabaseConversionOutcome ConvertIfNeeded(string databasePath, string password, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(logger);

        var path = Path.GetFullPath(databasePath);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return DatabaseConversionOutcome.NoDatabase;
        }

        if (IsPlaintext(path))
        {
            throw new DatabaseConversionException(
                $"{Path.GetFileName(path)} is not encrypted, but MemoryMcp:Encryption:Enabled is true. " +
                "Turn encryption off or restore the encrypted database. Nothing was changed.");
        }

        // A journal or WAL beside the database means an unfinished transaction. Opening the file would replay
        // it, and with the wrong cipher that writes garbage pages, so such a database is only examined on a copy.
        if (!HasSidecar(path) && CanOpen(CurrentConnectionString(path, password, SqliteOpenMode.ReadOnly)))
        {
            return DatabaseConversionOutcome.AlreadyCurrent;
        }

        using var conversionLock = AcquireLock(path);
        try
        {
            if (HasSidecar(path))
            {
                return ExamineOnCopy(path, password);
            }

            // Another server instance may have converted the database while this one waited for the lock.
            if (CanOpen(CurrentConnectionString(path, password, SqliteOpenMode.ReadOnly)))
            {
                return DatabaseConversionOutcome.AlreadyCurrent;
            }

            if (!CanOpen(SqlCipherConnectionString(path, password, SqliteOpenMode.ReadOnly)))
            {
                throw CannotOpen(path);
            }

            var backup = ConvertToCurrentFormat(path, password);
            logger.LogInformation(
                "Converted {Database} from the SQLCipher format to the SQLite3 Multiple Ciphers format. The original is kept as {Backup} (same key); delete it once the converted database has proven itself.",
                path,
                backup);
            return DatabaseConversionOutcome.Converted;
        }
        catch (SqliteException ex)
        {
            throw new DatabaseConversionException(
                $"{Path.GetFileName(path)} could not be converted: {ex.Message}. Nothing was changed.", ex);
        }
    }

    private static string ConvertToCurrentFormat(string path, string password)
    {
        var converting = path + ConvertingSuffix;
        DeleteWithSidecars(converting);
        var stamp = StampOf(path);

        try
        {
            Dictionary<string, long> expected;
            int sourceProblems;
            using (var source = Open(SqlCipherConnectionString(path, password, SqliteOpenMode.ReadOnly)))
            {
                sourceProblems = IntegrityProblemCount(source);
                expected = TableCounts(source);
                Execute(source, $"VACUUM main INTO '{VacuumTarget(converting, password)}';");
            }

            FlushToDisk(converting);
            if (!Verifies(converting, password, expected, sourceProblems))
            {
                throw new DatabaseConversionException(
                    $"The converted copy of {Path.GetFileName(path)} did not match the original. Nothing was changed.");
            }

            if (StampOf(path) != stamp)
            {
                throw new DatabaseConversionException(
                    $"{Path.GetFileName(path)} changed while it was being converted. Close every other session that uses the memory server and start again. Nothing was changed.");
            }

            var backup = NextBackupPath(path);
            Replace(converting, path, backup);
            return backup;
        }
        catch
        {
            DeleteWithSidecars(converting);
            throw;
        }
    }

    private static void Replace(string converting, string path, string backup)
    {
        try
        {
            File.Replace(converting, path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed replace can leave the original under the backup name; put it back.
            if (!File.Exists(path) && File.Exists(backup))
            {
                File.Move(backup, path);
            }

            throw new DatabaseConversionException(
                $"{Path.GetFileName(path)} is in use by another process, so the converted copy could not replace it. " +
                "Close every other session that uses the memory server and start again. Nothing was changed.",
                ex);
        }
    }

    private static DatabaseConversionOutcome ExamineOnCopy(string path, string password)
    {
        var probe = path + ProbeSuffix;
        try
        {
            CopyWithSidecars(path, probe);
            if (CanOpen(CurrentConnectionString(probe, password, SqliteOpenMode.ReadWrite)))
            {
                return DatabaseConversionOutcome.AlreadyCurrent;
            }

            CopyWithSidecars(path, probe);
            if (CanOpen(SqlCipherConnectionString(probe, password, SqliteOpenMode.ReadWrite)))
            {
                throw new DatabaseConversionException(
                    $"{Path.GetFileName(path)} is a SQLCipher database with an unfinished transaction (a -journal or -wal file lies beside it). " +
                    "Start the previous server version once so it completes the transaction, then start this version again. Nothing was changed.");
            }

            throw CannotOpen(path);
        }
        finally
        {
            DeleteWithSidecars(probe);
        }
    }

    private static DatabaseConversionException CannotOpen(string path) =>
        new($"{Path.GetFileName(path)} cannot be opened with the stored encryption key, neither in the current format nor as a SQLCipher database. Nothing was changed.");

    private static FileStream AcquireLock(string path)
    {
        var lockPath = path + LockSuffix;
        var deadline = DateTime.UtcNow + lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new DatabaseConversionException(
                        $"Another process has been converting {Path.GetFileName(path)} for more than {lockTimeout.TotalMinutes:0} minutes. Nothing was changed.",
                        ex);
                }

                Thread.Sleep(lockRetryDelay);
            }
        }
    }

    private static bool IsPlaintext(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static bool HasSidecar(string path) =>
        File.Exists(path + "-journal") || File.Exists(path + "-wal");

    private static string CurrentConnectionString(string path, string password, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Password = password,
            Pooling = false,
        }.ToString();

    // SQLCipher 4 defaults (PBKDF2-HMAC-SHA512, 256,000 iterations, HMAC-SHA512 page MACs): what
    // SQLitePCLRaw.bundle_e_sqlcipher 2.1.x wrote for a passphrase key.
    private static string SqlCipherConnectionString(string path, string password, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = FileUri(path) + "?cipher=sqlcipher&legacy=4",
            Mode = mode,
            Password = password,
            Pooling = false,
        }.ToString();

    // The default cipher of SQLite3 Multiple Ciphers, which is what a plain Password connection uses.
    private static string VacuumTarget(string path, string password) =>
        (FileUri(path) + "?cipher=chacha20&key=" + Uri.EscapeDataString(password)).Replace("'", "''", StringComparison.Ordinal);

    private static string FileUri(string path) =>
        "file:" + Path.GetFullPath(path).Replace('\\', '/')
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("?", "%3f", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal);

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool CanOpen(string connectionString)
    {
        try
        {
            using var connection = Open(connectionString);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabase)
        {
            return false;
        }
    }

    private static bool Verifies(string path, string password, Dictionary<string, long> expected, int sourceProblems)
    {
        try
        {
            using var connection = Open(CurrentConnectionString(path, password, SqliteOpenMode.ReadOnly));
            var problems = IntegrityProblemCount(connection);
            return (problems == 0 || problems <= sourceProblems)
                && TableCounts(connection).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static int IntegrityProblemCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var problems = 0;
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                problems++;
            }
        }

        return problems;
    }

    // Virtual tables need their module (sqlite-vec is not loaded here); their shadow tables are ordinary
    // tables and are counted.
    private static Dictionary<string, long> TableCounts(SqliteConnection connection)
    {
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" NOT INDEXED;";
            counts[name] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static (long Length, long Ticks) StampOf(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string NextBackupPath(string path)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
        {
            return backup;
        }

        var stamped = backup + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var candidate = stamped;
        for (var attempt = 2; File.Exists(candidate); attempt++)
        {
            candidate = stamped + "-" + attempt.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static void CopyWithSidecars(string source, string target)
    {
        DeleteWithSidecars(target);
        File.Copy(source, target);
        foreach (var suffix in sidecarSuffixes)
        {
            if (File.Exists(source + suffix))
            {
                File.Copy(source + suffix, target + suffix);
            }
        }
    }

    private static void DeleteWithSidecars(string path)
    {
        File.Delete(path);
        foreach (var suffix in sidecarSuffixes)
        {
            File.Delete(path + suffix);
        }
    }
}
