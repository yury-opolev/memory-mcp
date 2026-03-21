using Microsoft.Data.Sqlite;
using System;

// Initialize SQLitePCLRaw with the SQLCipher bundle BEFORE opening any connections
SQLitePCL.Batteries_V2.Init();

Console.WriteLine("=== SQLCipher + sqlite-vec Compatibility Test ===");
Console.WriteLine();

// -------------------------------------------------------
// TEST 1: SQLCipher bundle WITH encryption key
// -------------------------------------------------------
Console.WriteLine("--- TEST 1: SQLCipher bundle WITH encryption (PRAGMA key) ---");
RunTest(encryptionKey: "test-key");

Console.WriteLine();

// -------------------------------------------------------
// TEST 2: SQLCipher bundle WITHOUT encryption key
// -------------------------------------------------------
Console.WriteLine("--- TEST 2: SQLCipher bundle WITHOUT encryption (no PRAGMA key) ---");
RunTest(encryptionKey: null);

Console.WriteLine();
Console.WriteLine("=== All tests completed ===");

static void RunTest(string? encryptionKey)
{
    try
    {
        // Step A: Open an in-memory SQLite connection
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Console.WriteLine("[PASS] Step A: Opened in-memory SQLite connection");

        // Print SQLite version info
        using (var verCmd = connection.CreateCommand())
        {
            verCmd.CommandText = "SELECT sqlite_version();";
            var version = verCmd.ExecuteScalar();
            Console.WriteLine($"       SQLite version: {version}");
        }

        // Step B: Set PRAGMA key (only if encryption key provided)
        if (encryptionKey != null)
        {
            using var keyCmd = connection.CreateCommand();
            keyCmd.CommandText = $"PRAGMA key = '{encryptionKey}';";
            keyCmd.ExecuteNonQuery();
            Console.WriteLine($"[PASS] Step B: Set PRAGMA key = '{encryptionKey}'");

            // Verify cipher is active
            using var cipherCmd = connection.CreateCommand();
            cipherCmd.CommandText = "PRAGMA cipher_version;";
            var cipherVersion = cipherCmd.ExecuteScalar();
            Console.WriteLine($"       Cipher version: {cipherVersion ?? "(null - cipher may not be active)"}");
        }
        else
        {
            Console.WriteLine("[SKIP] Step B: No encryption key (testing unencrypted with SQLCipher bundle)");
        }

        // Step C: Load the sqlite-vec extension
        try
        {
            connection.LoadExtension("vec0");
            Console.WriteLine("[PASS] Step C: Loaded sqlite-vec extension via LoadExtension(\"vec0\")");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Step C: Failed to load sqlite-vec extension via \"vec0\": {ex.Message}");
            Console.WriteLine("       Trying alternative extension name...");

            try
            {
                connection.LoadExtension("sqlite_vec");
                Console.WriteLine("[PASS] Step C: Loaded sqlite-vec extension via LoadExtension(\"sqlite_vec\")");
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[FAIL] Step C: Failed to load sqlite-vec via all methods.");
                Console.WriteLine($"       Error 1 (vec0): {ex.Message}");
                Console.WriteLine($"       Error 2 (sqlite_vec): {ex2.Message}");
                return;
            }
        }

        // Verify vec extension loaded
        try
        {
            using var vecVerCmd = connection.CreateCommand();
            vecVerCmd.CommandText = "SELECT vec_version();";
            var vecVersion = vecVerCmd.ExecuteScalar();
            Console.WriteLine($"       sqlite-vec version: {vecVersion}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       Could not query vec_version(): {ex.Message}");
        }

        // Step D: Create a virtual table using vec0
        try
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = "CREATE VIRTUAL TABLE test_vec USING vec0(embedding float[4]);";
            createCmd.ExecuteNonQuery();
            Console.WriteLine("[PASS] Step D: Created virtual table test_vec USING vec0(embedding float[4])");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Step D: Failed to create virtual table: {ex.Message}");
            return;
        }

        // Step E: Insert a test vector
        try
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO test_vec(rowid, embedding) VALUES (1, '[1.0, 0.0, 0.0, 0.0]');";
            insertCmd.ExecuteNonQuery();

            using var insertCmd2 = connection.CreateCommand();
            insertCmd2.CommandText = "INSERT INTO test_vec(rowid, embedding) VALUES (2, '[0.0, 1.0, 0.0, 0.0]');";
            insertCmd2.ExecuteNonQuery();

            using var insertCmd3 = connection.CreateCommand();
            insertCmd3.CommandText = "INSERT INTO test_vec(rowid, embedding) VALUES (3, '[0.0, 0.0, 1.0, 0.0]');";
            insertCmd3.ExecuteNonQuery();

            Console.WriteLine("[PASS] Step E: Inserted 3 test vectors");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Step E: Failed to insert test vector: {ex.Message}");
            return;
        }

        // Step F: Query with KNN search
        try
        {
            using var queryCmd = connection.CreateCommand();
            queryCmd.CommandText = @"
                SELECT rowid, distance
                FROM test_vec
                WHERE embedding MATCH '[1.0, 0.0, 0.0, 0.0]'
                    AND k = 3
                ORDER BY distance;
            ";

            using var reader = queryCmd.ExecuteReader();
            Console.WriteLine("[PASS] Step F: KNN search executed successfully. Results:");
            while (reader.Read())
            {
                var rowid = reader.GetInt64(0);
                var distance = reader.GetFloat(1);
                Console.WriteLine($"       rowid={rowid}, distance={distance}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Step F: KNN query failed: {ex.Message}");
            return;
        }

        Console.WriteLine("[PASS] All steps completed successfully!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Unexpected error: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"       Stack trace: {ex.StackTrace}");
    }
}
