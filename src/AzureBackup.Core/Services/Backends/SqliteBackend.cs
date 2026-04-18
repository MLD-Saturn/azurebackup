using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// Option C / C-1a foundation: SQLite + SQLCipher backend skeleton.
///
/// <para>
/// This class only proves the encryption stack works end-to-end on .NET 10:
/// open an encrypted file, create the schema, close, reopen with the same
/// password, and read back. The full <c>LocalDatabaseService</c> surface is
/// implemented incrementally in C-1b onwards behind the same backend
/// abstraction so we can keep all 536 existing tests passing throughout.
/// </para>
///
/// <para>
/// Encryption: SQLCipher Community Edition via
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c>. The Argon2id-derived key is
/// passed via <c>PRAGMA key</c> using the raw-byte hex literal form
/// (<c>x'…'</c>) so SQLCipher skips its built-in PBKDF2 pass - we have
/// already done the stronger Argon2id KDF on the way in.
/// </para>
/// </summary>
internal sealed class SqliteBackend : IDisposable
{
    // Argon2id parameters - identical to LocalDatabaseService so the same
    // password derives the same key during migration.
    private const int Argon2DegreeOfParallelism = 8;
    private const int Argon2MemorySize = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int SaltSize = 16;
    private const int DerivedKeySize = 32;

    private SqliteConnection? _connection;
    private string? _databasePath;

    /// <summary>
    /// Salt file lives next to the database, identical convention to the
    /// LiteDB backend so an upgrading user's existing salt continues to work.
    /// </summary>
    private static string GetSaltFilePath(string databasePath) => databasePath + ".salt";

    public bool IsInitialized => _connection != null;
    public string? DatabasePath => _databasePath;

    /// <summary>
    /// Opens (or creates) the encrypted SQLite database at
    /// <paramref name="databasePath"/>. Derives the encryption key from
    /// <paramref name="password"/> using Argon2id and the stored salt; if the
    /// salt file does not exist a fresh one is generated.
    /// </summary>
    public void Initialize(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        _databasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var salt = LoadOrCreateSalt(databasePath);
        var derivedKey = DeriveKeyFromPassword(password, salt);
        try
        {
            OpenAndUnlock(databasePath, derivedKey);
            ApplyPragmas();
            CreateSchema();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Closes the underlying connection and releases native handles.
    /// Safe to call multiple times.
    /// </summary>
    public void Close()
    {
        if (_connection != null)
        {
            // Force a final WAL checkpoint so the next open is fast and the
            // -wal / -shm files do not linger.
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort; never throw from Close.
            }

            _connection.Dispose();
            _connection = null;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Test hook: returns SQLCipher's reported version, or null if the loaded
    /// SQLite native library is not SQLCipher (i.e. encryption is silently
    /// not happening).
    /// </summary>
    internal string? ReadSqlcipherVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA cipher_version;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Test hook: confirms the connection is open by reading SQLite's version
    /// string. Used by the C-1a smoke test to prove end-to-end open + close
    /// works without exposing the connection.
    /// </summary>
    internal string ReadSqliteVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var result = (string?)cmd.ExecuteScalar();
        return result ?? string.Empty;
    }

    /// <summary>
    /// Test hook: confirms the schema was created by counting expected tables.
    /// </summary>
    internal int CountSchemaTables()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static byte[] LoadOrCreateSalt(string databasePath)
    {
        var saltFilePath = GetSaltFilePath(databasePath);
        if (File.Exists(saltFilePath))
        {
            var salt = File.ReadAllBytes(saltFilePath);
            if (salt.Length != SaltSize)
            {
                throw new InvalidOperationException(
                    $"Database salt file is corrupted (expected {SaltSize} bytes, got {salt.Length})");
            }
            return salt;
        }

        var fresh = new byte[SaltSize];
        RandomNumberGenerator.Fill(fresh);
        File.WriteAllBytes(saltFilePath, fresh);
        return fresh;
    }

    private static byte[] DeriveKeyFromPassword(ReadOnlySpan<char> password, byte[] salt)
    {
        var passwordBytes = PasswordBytes.FromChars(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations,
            };
            return argon2.GetBytes(DerivedKeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private void OpenAndUnlock(string databasePath, byte[] derivedKey)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            // Disable connection pooling. We manage exactly one connection
            // per backend instance; pooling would silently hand a previously
            // unlocked connection back to a new SqliteBackend instance,
            // bypassing the PRAGMA key check and breaking wrong-password
            // detection. Verified by an early version of the wrong-password
            // smoke test that "passed" only because the same pooled
            // connection was reused across instances.
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            // SQLCipher: tune the KDF BEFORE the key pragma. SQLCipher applies
            // these settings to the key-derivation step, so reordering breaks
            // it (default 256000 PBKDF2 rounds would be applied instead).
            //
            // We use PBKDF2 mode (rather than raw-key `x'...'`) because raw-key
            // mode does NOT validate the key on open - SQLCipher silently
            // decrypts pages into garbage with the wrong key. PBKDF2 mode
            // writes a verification HMAC into page 1 so wrong keys fail
            // cleanly with SQLITE_NOTADB. We set kdf_iter=1 to skip the heavy
            // PBKDF2 work because our Argon2id pass already did the strong KDF.
            using (var keyCmd = connection.CreateCommand())
            {
                keyCmd.CommandText =
                    "PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA256;" +
                    "PRAGMA kdf_iter = 1;";
                keyCmd.ExecuteNonQuery();

                // Pass the derived key as a quoted base64 string. Use
                // SqliteParameter so any quote characters in the base64 output
                // (none, but defensive) cannot break the SQL.
                keyCmd.CommandText = "SELECT quote($key);";
                keyCmd.Parameters.AddWithValue("$key", Convert.ToBase64String(derivedKey));
                var quoted = (string?)keyCmd.ExecuteScalar();
                keyCmd.Parameters.Clear();
                keyCmd.CommandText = $"PRAGMA key = {quoted};";
                keyCmd.ExecuteNonQuery();
            }

            // Verify the key actually unlocked the database. With PBKDF2 mode
            // SQLCipher validates page 1's HMAC on the first physical page
            // read. ExecuteScalar on a SELECT returning zero rows does NOT
            // count - it can hit a parsed-schema cache without forcing a
            // page-1 decrypt. We use ExecuteReader and actually iterate so
            // the engine reads encrypted pages off disk; a wrong key surfaces
            // as SQLITE_NOTADB (26) at that point.
            var dbExistedBeforeOpen = new FileInfo(databasePath).Length > 0;
            if (dbExistedBeforeOpen)
            {
                try
                {
                    using var probe = connection.CreateCommand();
                    // sqlite_master always has at least one entry on a
                    // previously-initialised DB (the seeded config table).
                    probe.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                    using var reader = probe.ExecuteReader();
                    var sawAtLeastOneRow = false;
                    while (reader.Read())
                    {
                        sawAtLeastOneRow = true;
                    }
                    if (!sawAtLeastOneRow)
                    {
                        // The DB file existed but no tables were visible.
                        // Either the file is genuinely empty (impossible -
                        // CreateSchema runs on every Initialize) or the key
                        // was wrong and SQLCipher decrypted garbage that
                        // happened to parse as an empty schema.
                        connection.Dispose();
                        throw new InvalidPasswordException("Invalid password. Please try again.");
                    }
                }
                catch (SqliteException ex)
                {
                    connection.Dispose();
                    if (ex.SqliteErrorCode == 26 ||
                        (ex.Message?.Contains("not a database", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (ex.Message?.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        throw new InvalidPasswordException("Invalid password. Please try again.", ex);
                    }
                    throw;
                }
            }

            _connection = connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ApplyPragmas()
    {
        if (_connection == null) return;

        // WAL journaling: concurrent readers + single writer, fast commits,
        // matches the rationale for Phase 5's RWLock work.
        // foreign_keys: required for ON DELETE CASCADE to work; off by default.
        // synchronous=NORMAL: safe with WAL, much faster than FULL.
        // temp_store=MEMORY: avoid disk for sort/group temporaries.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            """;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        if (_connection == null) return;

        // Pure-relational schema (Option C / §4 of docs/option-c-evaluation.md).
        // Ordered so foreign-key dependencies are created before their referrers.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- Single-row configuration table; row id is always 1.
            CREATE TABLE IF NOT EXISTS config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                encrypted_connection_string BLOB NULL,
                storage_account_name TEXT NULL,
                container_name TEXT NULL,
                use_entra_id_auth INTEGER NOT NULL DEFAULT 0,
                last_backup_time TEXT NULL,
                total_bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                schema_version INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                storage_tier INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                added_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS watched_folder_excludes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_id INTEGER NOT NULL,
                pattern TEXT NOT NULL,
                kind INTEGER NOT NULL,  -- 0 = ExcludePatterns, 1 = ExcludeSubfolders
                FOREIGN KEY (folder_id) REFERENCES watched_folders(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_watched_folder_excludes_folder
                ON watched_folder_excludes(folder_id);

            CREATE TABLE IF NOT EXISTS global_exclude_patterns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pattern TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                local_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL,
                last_modified TEXT NOT NULL,
                file_hash TEXT NULL,
                status INTEGER NOT NULL,
                backed_up_at TEXT NULL,
                error_message TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);
            CREATE INDEX IF NOT EXISTS idx_files_file_hash ON files(file_hash);

            CREATE TABLE IF NOT EXISTS file_chunks (
                file_id INTEGER NOT NULL,
                chunk_order INTEGER NOT NULL,
                chunk_index INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                hash TEXT NOT NULL,
                blob_name TEXT NULL,
                PRIMARY KEY (file_id, chunk_order),
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_file_chunks_hash ON file_chunks(hash);

            CREATE TABLE IF NOT EXISTS pending_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                change_type INTEGER NOT NULL,
                detected_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_pending_changes_path ON pending_changes(file_path);

            CREATE TABLE IF NOT EXISTS chunk_index (
                chunk_hash TEXT PRIMARY KEY,
                first_uploaded_at TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                reference_count INTEGER NOT NULL,
                current_tier INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_index_refcount ON chunk_index(reference_count);
            CREATE INDEX IF NOT EXISTS idx_chunk_index_tier ON chunk_index(current_tier);

            -- Reverse index built in Phase 5 / P3; carried over directly so the
            -- migration is a one-to-one row copy.
            CREATE TABLE IF NOT EXISTS chunk_file_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                chunk_hash TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                referenced_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_path ON chunk_file_refs(file_path);
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_hash ON chunk_file_refs(chunk_hash);

            CREATE TABLE IF NOT EXISTS index_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Seed the singleton config row so the wrong-password probe in
        // OpenAndUnlock has a real user-table read to perform on reopen.
        // INSERT OR IGNORE keeps this idempotent across reopens.
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT OR IGNORE INTO config (id, schema_version) VALUES (1, 1);
            """;
        seed.ExecuteNonQuery();
    }
}
