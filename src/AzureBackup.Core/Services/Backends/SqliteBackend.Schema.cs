using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static AzureBackup.Core.KdfParameters;
using static AzureBackup.Core.Services.KdfMemoryDiagnostics;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B22: schema, KDF, and SQLCipher unlock surface for
/// <see cref="SqliteBackend"/>. Split from the root partial to keep each
/// file focused (the root file owns lifecycle, this one owns the
/// open/setup pipeline).
/// </summary>
internal sealed partial class SqliteBackend
{
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

    private byte[] DeriveKeyFromPassword(ReadOnlySpan<char> password, byte[] salt)
        // B22: cannot pass EmitDiag as a method group because it is
        // [Conditional("DIAGNOSTICLOG")] (CS1618). Wrap it in a lambda
        // so the call site is preserved, and let the lambda body be a
        // no-op when DIAGNOSTICLOG is undefined (the EmitDiag call
        // itself is then compiled away).
        => DeriveKeyFromPasswordCore(passwordChars: password, salt: salt, diag: msg => EmitDiag(msg));

    private static byte[] DeriveKeyFromPasswordCore(
        ReadOnlySpan<char> passwordChars, byte[] salt, Action<string>? diag)
    {
        var passwordBytes = PasswordBytes.FromChars(passwordChars);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation";
        var workingSetMb = ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
        var managedMb = ToMegabytes(GC.GetTotalMemory(false));
        diag?.Invoke($"DeriveKey: starting Argon2id (memory={Argon2MemorySize / 1024} MB, lanes={Argon2DegreeOfParallelism}, " +
                     $"iterations={Argon2Iterations}, gcMode={gcMode}, workingSet={workingSetMb} MB, managedHeap={managedMb} MB)");
        try
        {
            // B11/B12: see comment block in EncryptionService.DeriveKeyAsync
            // for the LOH-compaction-on-OOM rationale. We never silently
            // weaken parameters because that would change the derived key
            // and lock the user out of their existing database.
            Exception? lastOom = null;
            try
            {
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = salt,
                    DegreeOfParallelism = Argon2DegreeOfParallelism,
                    MemorySize = Argon2MemorySize,
                    Iterations = Argon2Iterations,
                };
                var key = argon2.GetBytes(DerivedKeySize);
                diag?.Invoke($"DeriveKey: completed in {sw.ElapsedMilliseconds} ms");
                return key;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                diag?.Invoke($"DeriveKey: OutOfMemoryException at {sw.ElapsedMilliseconds} ms -- {ex.Message}");
                diag?.Invoke($"  Pre-compaction: workingSet={ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64)} MB, " +
                             $"GC managed={ToMegabytes(GC.GetTotalMemory(false))} MB, " +
                             $"GC available={ToMegabytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} MB, " +
                             $"loh fragmented={ToMegabytes(GC.GetGCMemoryInfo().FragmentedBytes)} MB");
                ForceLargeObjectHeapCompaction();
                diag?.Invoke($"  Post-compaction: workingSet={ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64)} MB, " +
                             $"GC managed={ToMegabytes(GC.GetTotalMemory(false))} MB, " +
                             $"GC available={ToMegabytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} MB, " +
                             $"loh fragmented={ToMegabytes(GC.GetGCMemoryInfo().FragmentedBytes)} MB");
            }

            try
            {
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = salt,
                    DegreeOfParallelism = Argon2DegreeOfParallelism,
                    MemorySize = Argon2MemorySize,
                    Iterations = Argon2Iterations,
                };
                var key = argon2.GetBytes(DerivedKeySize);
                diag?.Invoke($"DeriveKey: completed (after LOH compaction) in {sw.ElapsedMilliseconds} ms");
                return key;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                diag?.Invoke($"DeriveKey: OutOfMemoryException AFTER LOH compaction at {sw.ElapsedMilliseconds} ms -- giving up");
            }

            throw new InsufficientMemoryForKdfException(
                BuildOomDiagnostic("database key", Argon2MemorySize),
                lastOom);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// B12: produce a diagnostic message that contradicts the bare
    /// "Insufficient memory" runtime text by reporting actual process
    /// memory state. If the OS reports plenty of free memory but the
    /// process still cannot allocate, the failure is LOH fragmentation
    /// (or the debugger's Diagnostic Tools window pinning memory),
    /// which the user can address.
    /// </summary>
    private static string BuildOomDiagnostic(string what, int kdfMemoryKb)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var workingSetMb = ToMegabytes(proc.WorkingSet64);
            var privateMb = ToMegabytes(proc.PrivateMemorySize64);
            var gcMb = ToMegabytes(GC.GetTotalMemory(forceFullCollection: false));
            var memInfo = GC.GetGCMemoryInfo();
            var totalAvailableMb = ToMegabytes(memInfo.TotalAvailableMemoryBytes);

            return $"Unable to derive the {what}: Argon2id key derivation could not allocate " +
                   $"its {kdfMemoryKb / 1024} MB working memory after a forced LOH compaction. " +
                   $"Process state: workingSet={workingSetMb} MB, privateBytes={privateMb} MB, " +
                   $"GC managed={gcMb} MB, GC reports {totalAvailableMb} MB available. " +
                   $"If 'available' is high, the cause is Large Object Heap fragmentation -- " +
                   $"common when running under the Visual Studio debugger with Diagnostic Tools open. " +
                   $"Close Diagnostic Tools (Debug > Windows > Show Diagnostic Tools), or run " +
                   $"the app outside the debugger.";
        }
        catch
        {
            return $"Unable to derive the {what}: Argon2id key derivation could not allocate " +
                   $"its {kdfMemoryKb / 1024} MB working memory. Close other applications, " +
                   $"close VS Diagnostic Tools, or restart the machine.";
        }
    }

    private void OpenAndUnlock(string databasePath, byte[] derivedKey, bool dbExistedBeforeOpen)
    {
        // B10: only allow Create when there is no DB on disk yet. A
        // wrong-password attempt against an EXISTING database must not
        // be able to write a fresh empty file as a side effect; using
        // SqliteOpenMode.ReadWrite (no Create) means connection.Open()
        // throws SqliteException(14, "unable to open database file") on
        // a missing file rather than silently creating one.
        var mode = dbExistedBeforeOpen
            ? SqliteOpenMode.ReadWrite
            : SqliteOpenMode.ReadWriteCreate;
        EmitDiag($"OpenAndUnlock: opening with mode={mode} (dbExistedBeforeOpen={dbExistedBeforeOpen})");
        _connection = OpenAndUnlockCore(
            databasePath, derivedKey, mode, validateKey: true);
        EmitDiag("OpenAndUnlock: connection unlocked successfully");
    }

    /// <summary>
    /// Benchmark-only: opens a read-only SQLCipher-keyed connection to an
    /// existing database. Re-uses the writer's exact key-derivation +
    /// PRAGMA-tuning sequence so a connection opened by this method is
    /// guaranteed to read pages the writer wrote.
    ///
    /// <para>
    /// Used by C-3 (4/N) <c>SqlitePooledReader</c> to spin up a pool of
    /// parallel-read connections against an already-initialised database.
    /// NOT exposed via <see cref="IDatabaseBackend"/>: production code
    /// would build a managed pool with rent/return semantics; the
    /// benchmark only needs raw connections.
    /// </para>
    ///
    /// <para>
    /// Each returned connection has been issued <c>PRAGMA key</c> and a
    /// page-decrypt probe. Caller owns the connection lifetime.
    /// </para>
    /// </summary>
    internal static SqliteConnection OpenReadOnlyForBenchmark(
        string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Database does not exist", databasePath);

        var salt = LoadOrCreateSalt(databasePath);
        var derivedKey = DeriveKeyFromPasswordCore(passwordChars: password, salt: salt, diag: null);
        try
        {
            return OpenAndUnlockCore(
                databasePath, derivedKey, SqliteOpenMode.ReadOnly, validateKey: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// B51: opens a quarantined catalog read-only using a caller-supplied
    /// salt sidecar (NOT the on-disk <c>.salt</c> next to the file) and
    /// returns the single value the rebuild flow needs to talk to Azure:
    /// the 16-byte <c>password_salt</c> stored inside the encrypted
    /// <c>config</c> table. Every other field is recoverable from Azure
    /// or re-enterable by the user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rebuild flow lets the user point at a quarantined database
    /// (<c>backup.db.quarantine-yyyyMMdd-HHmmss</c>) plus the matching
    /// quarantined salt sidecar
    /// (<c>backup.db.salt.quarantine-yyyyMMdd-HHmmss</c>). The two files
    /// were renamed atomically by <c>QuarantineCorruptDatabase</c>, so
    /// pairing them here is a user choice -- this helper does not infer
    /// the sidecar from the database path.
    /// </para>
    /// <para>
    /// The connection is opened with <see cref="SqliteOpenMode.ReadOnly"/>
    /// so a partial decrypt cannot mutate the quarantined bytes. The
    /// validate-key probe (page-1 HMAC check) inside
    /// <see cref="OpenAndUnlockCore"/> is the password-correctness
    /// oracle: a wrong password surfaces as
    /// <see cref="InvalidPasswordException"/> rather than allowing a
    /// "partial decrypt" path through.
    /// </para>
    /// </remarks>
    /// <param name="quarantinedDatabasePath">Path to the quarantined catalog file.</param>
    /// <param name="quarantinedSaltPath">Path to the matching quarantined salt sidecar (16 bytes).</param>
    /// <param name="password">The password the quarantined catalog was protected with.</param>
    /// <returns>
    /// The 16-byte <c>config.password_salt</c> if it could be read, or
    /// <c>null</c> if the row exists but the column is NULL (no Azure
    /// content was ever encrypted with this catalog).
    /// </returns>
    /// <exception cref="ArgumentException">A path or the password is null/whitespace.</exception>
    /// <exception cref="FileNotFoundException">Either file is missing.</exception>
    /// <exception cref="InvalidOperationException">The salt sidecar is the wrong size.</exception>
    /// <exception cref="InvalidPasswordException">The password does not match the quarantined catalog.</exception>
    public static byte[]? ReadPasswordSaltFromQuarantinedCatalog(
        string quarantinedDatabasePath,
        string quarantinedSaltPath,
        ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinedDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinedSaltPath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));
        if (!File.Exists(quarantinedDatabasePath))
            throw new FileNotFoundException(
                "Quarantined database file does not exist.", quarantinedDatabasePath);
        if (!File.Exists(quarantinedSaltPath))
            throw new FileNotFoundException(
                "Quarantined salt sidecar does not exist.", quarantinedSaltPath);

        var salt = File.ReadAllBytes(quarantinedSaltPath);
        if (salt.Length != SaltSize)
        {
            throw new InvalidOperationException(
                $"Quarantined salt sidecar is the wrong size " +
                $"(expected {SaltSize} bytes, got {salt.Length}). " +
                "The sidecar may belong to a different quarantine event.");
        }

        var derivedKey = DeriveKeyFromPasswordCore(passwordChars: password, salt: salt, diag: null);
        try
        {
            // ReadOnly mode + caller-supplied salt: we never write to the
            // quarantined files. The validate-key probe is what gives the
            // user a hard "wrong password" signal instead of letting a
            // garbage-decrypted page reach the SELECT below.
            using var connection = OpenAndUnlockCore(
                quarantinedDatabasePath, derivedKey,
                SqliteOpenMode.ReadOnly, validateKey: true);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT password_salt FROM config WHERE id = 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return null;
            }

            return (byte[])reader.GetValue(0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    private static SqliteConnection OpenAndUnlockCore(
        string databasePath, byte[] derivedKey,
        SqliteOpenMode mode, bool validateKey)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
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
            if (validateKey && dbExistedBeforeOpen)
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
                    // Wrong-key classification on the FIRST page-1 read after
                    // PRAGMA key. SQLCipher rejects bad keys cleanly as
                    // SQLITE_NOTADB (26) only when the decrypt produces bytes
                    // that obviously don't look like a SQLite header. When the
                    // garbage decrypt happens to parse as a partially-valid
                    // header but with internally inconsistent b-tree pointers
                    // SQLite raises SQLITE_CORRUPT (11) "database disk image
                    // is malformed" instead. Both shapes mean "wrong password"
                    // here -- a real on-disk corruption would not have shifted
                    // its surface based on whether the user typed the right or
                    // wrong password. Real bug observed by tester after the
                    // B47 LiteDB-probe removal stopped masking this exception.
                    //
                    // Anything code 11 surfaces from LATER reads (the schema-
                    // creation pass below, ApplyPragmas, or steady-state
                    // queries) is genuine plaintext-image corruption and
                    // propagates unchanged; only the validate-key probe
                    // remaps it to InvalidPasswordException.
                    if (ex.SqliteErrorCode == 26 ||
                        ex.SqliteErrorCode == 11 ||
                        (ex.Message?.Contains("not a database", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (ex.Message?.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (ex.Message?.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        throw new InvalidPasswordException("Invalid password. Please try again.", ex);
                    }
                    throw;
                }
                catch (OverflowException ex)
                {
                    // Wrong password where SQLCipher decrypted garbage that
                    // happened to look like a valid schema header but whose
                    // page-size or column-length fields parse as huge values.
                    // SQLite-managed-driver translates these into "Array
                    // dimensions exceeded supported range" rather than its
                    // own SqliteException. Without this catch the user sees
                    // the opaque OverflowException message and assumes the
                    // app is broken rather than that they typed the wrong
                    // password (real bug observed by tester).
                    connection.Dispose();
                    throw new InvalidPasswordException("Invalid password. Please try again.", ex);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException)
                {
                    // Same family as OverflowException above: garbage-decrypt
                    // values fed into array-allocation paths.
                    connection.Dispose();
                    throw new InvalidPasswordException("Invalid password. Please try again.", ex);
                }
            }

            return connection;
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
        // cache_size: -65536 means "65536 KiB of cache" (negative values are KB,
        //   positive values are PAGE COUNT). Default is 2000 pages = 8 MB which
        //   is too small for the rebuild + bulk-insert workloads we measured in
        //   C-3 (3b): the 100K and 500K cells spilled the page cache and paid
        //   significant decrypt-thrash cost. 64 MB sized to comfortably hold
        //   the working set of the largest one-time migration step (~50 MB at
        //   500K chunks). The cache lives in the SQLite/SQLCipher allocator,
        //   not the .NET GC heap, so this does NOT show up as managed
        //   allocations in benchmark results.
        EmitDiag("ApplyPragmas: setting WAL journaling, foreign_keys=ON, synchronous=NORMAL, cache_size=-65536 KiB");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -65536;
            """;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        if (_connection == null) return;

        EmitDiag("CreateSchema: starting CREATE TABLE IF NOT EXISTS pass");
        // Pure-relational schema (Option C / §4 of docs/option-c-evaluation.md).
        // Ordered so foreign-key dependencies are created before their referrers.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- Single-row configuration table; row id is always 1.
            -- Mirrors every field on Models.BackupConfiguration (see C-1c
            -- commit message for the field-by-field mapping). Defaults match
            -- the C# model defaults so a freshly seeded row reads back as the
            -- equivalent of `new BackupConfiguration()`.
            CREATE TABLE IF NOT EXISTS config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                auth_method INTEGER NOT NULL DEFAULT 1,            -- AzureAuthMethod, 1 = ConnectionString
                storage_account_name TEXT NULL,
                encrypted_connection_string BLOB NULL,
                container_name TEXT NULL DEFAULT 'backup',
                password_salt BLOB NULL,
                password_verification_hash BLOB NULL,
                last_backup_time TEXT NULL,
                total_bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                failed_login_attempts INTEGER NOT NULL DEFAULT 0,
                lockout_until_ticks INTEGER NULL,
                is_entra_id_authenticated INTEGER NOT NULL DEFAULT 0,
                entra_id_user_name TEXT NULL,
                config_version INTEGER NOT NULL DEFAULT 3,
                memory_limit_enabled INTEGER NOT NULL DEFAULT 1,
                -- B29: NULL = "user has not yet expressed a preference,
                -- compute a hardware-aware default at read time via
                -- SystemMemoryHelper.GetRecommendedDefaultLimitMB". A
                -- non-NULL integer here is always a value the user
                -- explicitly saved through the Settings UI (or that
                -- migrated forward from a pre-B29 database). Existing
                -- B27/B28 databases keep their stored 8192 (or whatever
                -- the user set) -- DEFAULT only fires for newly
                -- inserted rows, so no existing user is silently
                -- re-configured.
                memory_limit_mb INTEGER NULL DEFAULT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                storage_tier INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1
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
                blob_name TEXT NOT NULL DEFAULT '',
                file_size INTEGER NOT NULL,
                last_modified TEXT NOT NULL,
                file_hash TEXT NOT NULL DEFAULT '',
                status INTEGER NOT NULL,
                backed_up_at TEXT NOT NULL,
                metadata_version INTEGER NOT NULL DEFAULT 1
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
                original_uploader_path TEXT NOT NULL DEFAULT '',
                size_bytes INTEGER NOT NULL,
                reference_count INTEGER NOT NULL,
                current_tier INTEGER NOT NULL DEFAULT 0,
                last_verified_at TEXT NOT NULL,
                -- D6: MD5 of the encrypted blob as stored in Azure
                -- (matches what GetChunkPropertiesAsync returns as
                -- ContentHash). Stamped at upload time; null for
                -- chunks uploaded before D6. The integrity check's
                -- T1 tier compares this against the live Azure-side
                -- hash so same-size envelope corruption is detected
                -- without escalating to a full T2 download.
                expected_encrypted_md5 BLOB NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_index_refcount ON chunk_index(reference_count);
            CREATE INDEX IF NOT EXISTS idx_chunk_index_tier ON chunk_index(current_tier);

            -- Reverse index built in Phase 5 / P3; replaces the redundant
            -- ReferencingFiles list on ChunkIndexEntry (eval doc / §4:
            -- "What we drop from the LiteDB schema").
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

            -- Persisted history of post-backup integrity checks (D1).
            -- Retention: keep most recent 30 rows; older pruned by
            -- LocalDatabaseService.PruneIntegrityCheckRuns. Per-failure
            -- detail lives in integrity_check_failures, keyed by run_id.
            -- The companion .diag files in the diagnostics/ folder are
            -- the authoritative source for chunk-level traces.
            CREATE TABLE IF NOT EXISTS integrity_check_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NULL,
                session_id TEXT NOT NULL,
                scope_summary TEXT NOT NULL,
                files_checked INTEGER NOT NULL DEFAULT 0,
                files_passed INTEGER NOT NULL DEFAULT 0,
                files_failed_t1 INTEGER NOT NULL DEFAULT 0,
                files_failed_t2 INTEGER NOT NULL DEFAULT 0,
                files_failed_t3 INTEGER NOT NULL DEFAULT 0,
                files_warning INTEGER NOT NULL DEFAULT 0,
                files_auto_repaired INTEGER NOT NULL DEFAULT 0,
                cancelled INTEGER NOT NULL DEFAULT 0,
                parent_run_id INTEGER NULL,
                diag_bundle_path TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_integrity_check_runs_started ON integrity_check_runs(started_utc DESC);

            -- Per-file failure rows for the LATEST run only. When a new
            -- run starts the engine deletes every row in this table whose
            -- run_id != the new run id, keeping the failure panel
            -- responsive even if the user has dozens of historical runs.
            CREATE TABLE IF NOT EXISTS integrity_check_failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                local_path TEXT NOT NULL,
                failure_tier INTEGER NOT NULL,
                failure_reason TEXT NOT NULL,
                chunk_hash TEXT NULL,
                detail TEXT NOT NULL,
                diag_file_path TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_integrity_check_failures_run ON integrity_check_failures(run_id);
            CREATE INDEX IF NOT EXISTS idx_integrity_check_failures_tier ON integrity_check_failures(failure_tier);
            """;
        cmd.ExecuteNonQuery();

        // D6 backfill: idempotently add expected_encrypted_md5 to chunk_index
        // for databases created before D6. Skipped when the column already
        // exists (CREATE TABLE IF NOT EXISTS above declares it for fresh
        // installs). Pre-existing chunks remain null until they get
        // re-uploaded; the integrity check engine treats null as "T1
        // cannot decide -- pass" so legacy chunks still pass the cheap
        // tier (escalation to T2 is the user's lever).
        using (var probeCmd = _connection.CreateCommand())
        {
            probeCmd.CommandText = "SELECT 1 FROM pragma_table_info('chunk_index') WHERE name='expected_encrypted_md5';";
            var present = probeCmd.ExecuteScalar();
            if (present == null)
            {
                EmitDiag("CreateSchema: D6 backfill -- adding expected_encrypted_md5 column to chunk_index");
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE chunk_index ADD COLUMN expected_encrypted_md5 BLOB NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }

        // B43 backfill: idempotently add files_auto_repaired to
        // integrity_check_runs for databases created before B43. Default
        // 0 reflects the historical truth: pre-B43 runs had no
        // auto-repair counter to record (B42 introduced auto-repair but
        // only stored the count in-memory on the IntegrityCheckRun
        // returned to the caller). Existing rows therefore correctly
        // report 0 auto-repaired files; only B43+ runs persist a real
        // count.
        using (var probeCmd = _connection.CreateCommand())
        {
            probeCmd.CommandText = "SELECT 1 FROM pragma_table_info('integrity_check_runs') WHERE name='files_auto_repaired';";
            var present = probeCmd.ExecuteScalar();
            if (present == null)
            {
                EmitDiag("CreateSchema: B43 backfill -- adding files_auto_repaired column to integrity_check_runs");
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE integrity_check_runs ADD COLUMN files_auto_repaired INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }
        }

        // Seed the singleton config row so the wrong-password probe in
        // OpenAndUnlock has a real user-table read to perform on reopen.
        // INSERT OR IGNORE keeps this idempotent across reopens; the row
        // takes its column defaults so reading immediately gives back
        // the equivalent of `new BackupConfiguration()`.
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT OR IGNORE INTO config (id) VALUES (1);
            """;
        seed.ExecuteNonQuery();
        EmitDiag("CreateSchema: completed");
    }
}
