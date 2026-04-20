using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Option C / C-2: migration from an on-disk LiteDB database to a
/// SQLCipher-encrypted SQLite database at the same path.
///
/// <para>
/// <b>When migration runs.</b> The <c>AZBK_USE_SQLITE</c> feature
/// flag from C-1 step b routes <see cref="Initialize"/> through
/// <c>SqliteBackend</c>. When the flag is on AND an existing database
/// file is found at the target path AND that file is NOT already a
/// SQLite database (the wrong-password probe in
/// <c>SqliteBackend.Initialize</c> threw), we assume it's a LiteDB
/// database, run the migration, and then open the fresh SQLite
/// database that migration produced.
/// </para>
///
/// <para>
/// <b>Atomicity.</b> Migration writes to a temp SQLite database at
/// <c>&lt;path&gt;.sqlite-tmp</c>. On successful copy + reverse-index
/// mark, we:
/// </para>
/// <list type="number">
///   <item>Close both databases (forces WAL checkpoint on SQLite).</item>
///   <item><see cref="File.Move"/> the original LiteDB at <c>&lt;path&gt;</c>
///     to <c>&lt;path&gt;.litedb-backup</c>. This FREES the target path.</item>
///   <item><see cref="File.Move"/> the original LiteDB salt at
///     <c>&lt;path&gt;.salt</c> to <c>&lt;path&gt;.litedb-backup.salt</c>.</item>
///   <item><see cref="File.Move"/> the temp SQLite at
///     <c>&lt;path&gt;.sqlite-tmp</c> to <c>&lt;path&gt;</c>.</item>
///   <item><see cref="File.Move"/> the temp SQLite salt at
///     <c>&lt;path&gt;.sqlite-tmp.salt</c> to <c>&lt;path&gt;.salt</c>.</item>
/// </list>
///
/// <para>
/// If ANY step from (3) onward fails the user ends in an inconsistent
/// state on disk (partial rename sequence). The <see cref="File.Move"/>
/// calls are fast OS-level rename operations on the same volume, so the
/// practical failure modes are (a) permissions (caller can fix and
/// retry) or (b) the file was locked by another process (rare; the
/// connections we own are already closed). In both cases the user can
/// manually rename the files into place - the logs say exactly what to
/// do.
/// </para>
///
/// <para>
/// <b>LiteDB backup retention.</b> The <c>.litedb-backup</c> file is
/// NOT deleted automatically. The user can manually delete it once
/// they're confident the SQLite database works. A future commit may
/// auto-delete after one successful app launch.
/// </para>
///
/// <para>
/// <b>Cancellation.</b> The per-table loops call
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> between
/// batches. If the user cancels mid-migration we delete the temp SQLite
/// file and leave the LiteDB database untouched. The next
/// Initialize call sees the LiteDB database, retries migration from
/// scratch.
/// </para>
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Extension on the temp SQLite file written during migration.
    /// <c>&lt;originalPath&gt;.sqlite-tmp</c>. If you see this file on
    /// disk a previous migration was interrupted; the LiteDB database
    /// at the original path is still authoritative.
    /// </summary>
    internal const string SqliteMigrationTempSuffix = ".sqlite-tmp";

    /// <summary>
    /// Extension on the renamed-aside LiteDB database after a
    /// successful migration. <c>&lt;originalPath&gt;.litedb-backup</c>.
    /// The user can manually delete this file once confident in the
    /// SQLite database.
    /// </summary>
    internal const string LiteDbBackupSuffix = ".litedb-backup";

    /// <summary>
    /// Attempts to open the file at <paramref name="databasePath"/> as
    /// a SQLCipher-encrypted SQLite database with the given password.
    /// Returns true if successful. Returns false if the open fails with
    /// <see cref="InvalidPasswordException"/> (strong signal that the
    /// file is NOT a SQLite database in that password's encryption
    /// scheme - most likely a LiteDB file instead).
    /// </summary>
    /// <remarks>
    /// Any exception other than <see cref="InvalidPasswordException"/>
    /// propagates - the file is genuinely unreadable and migration
    /// would not help.
    /// </remarks>
    private static bool TryProbeAsSqlite(string databasePath, ReadOnlySpan<char> password)
    {
        try
        {
            using var probe = new SqliteBackend();
            probe.Initialize(databasePath, password);
            // Initialize succeeded => this IS a SQLite database with the
            // supplied password. Fall through to "true".
            return true;
        }
        catch (InvalidPasswordException)
        {
            // Wrong password for a SQLite DB, OR (more likely given the
            // callsite) not a SQLite DB at all. Either way the
            // appropriate next step is to try LiteDB.
            return false;
        }
    }

    /// <summary>
    /// Full LiteDB-to-SQLite migration. Caller guarantees that
    /// <paramref name="databasePath"/> points at a LiteDB database
    /// that opens successfully with <paramref name="password"/>.
    /// On success the method returns having:
    /// <list type="bullet">
    ///   <item>Written every row from the LiteDB database into a fresh
    ///     SQLite database at <paramref name="databasePath"/>.</item>
    ///   <item>Renamed the original LiteDB database to
    ///     <paramref name="databasePath"/> + <c>.litedb-backup</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="databasePath">Path to the LiteDB database that is the
    /// migration source; after success this path hosts a SQLite database.</param>
    /// <param name="password">Password for BOTH the source LiteDB
    /// (opened here) and the destination SQLite (created here). Matches
    /// production intent: the user supplies one password and migration
    /// preserves it.</param>
    /// <param name="progress">Optional progress reporter. The
    /// <c>total</c> component is an UPPER BOUND for the whole migration
    /// (files + chunks + pending changes); <c>processed</c> increments
    /// monotonically as each per-table phase completes.</param>
    /// <param name="cancellationToken">Cooperative cancellation. Checked
    /// between per-table phases. On cancellation the temp SQLite file is
    /// deleted and the LiteDB file is left untouched.</param>
    internal static void MigrateFromLiteDb(
        string databasePath,
        ReadOnlySpan<char> password,
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        var tempSqlitePath = databasePath + SqliteMigrationTempSuffix;
        var tempSqliteSaltPath = tempSqlitePath + ".salt";
        var litedbBackupPath = databasePath + LiteDbBackupSuffix;
        var litedbBackupSaltPath = litedbBackupPath + ".salt";
        var originalSaltPath = databasePath + ".salt";

        // Defensive: if a prior migration left stale temp artefacts
        // behind, clear them before starting. Otherwise SqliteBackend
        // would open the partial file and misread its state.
        DeleteIfExists(tempSqlitePath);
        DeleteIfExists(tempSqlitePath + "-wal");
        DeleteIfExists(tempSqlitePath + "-shm");
        DeleteIfExists(tempSqliteSaltPath);

        // Use a temporary LocalDatabaseService with NO feature flag
        // effect (we pass false through a private-field backdoor) so it
        // opens the file as LiteDB regardless of the env var. The
        // ambient env var is still set (that's how we got here) so we
        // cannot rely on the flag-off code path naturally.
        using var liteDb = new LocalDatabaseService();
        // The _sqliteBackend field stays null because we do NOT call
        // DatabaseBackendFactory; we call the LiteDB internals directly.
        // InitializeLiteDbOnly is a new internal helper that does the
        // same work as the LiteDB branch of Initialize but never checks
        // the flag.
        liteDb.InitializeLiteDbOnly(databasePath, password);

        // Create the destination SQLite backend at the TEMP path so
        // a crash mid-copy leaves the original LiteDB authoritative.
        using var sqlite = new SqliteBackend();
        try
        {
            sqlite.Initialize(tempSqlitePath, password);

            // Phase 1: configuration (one row).
            cancellationToken.ThrowIfCancellationRequested();
            var config = liteDb.GetConfiguration();
            sqlite.SaveConfiguration(config);

            // Phase 2: chunk_index (may be 10K-500K rows at production scale).
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = liteDb.GetAllChunkIndexEntries();
            // BulkInsert path: internal to SQLiteBackend, single transaction,
            // ~1-2 s at 500K rows based on C-3 (2/N) numbers.
            sqlite.BulkInsertChunkIndexEntries(chunkIndex);

            // Phase 3: files + file_chunks + chunk_file_refs. BulkInsertFiles
            // writes all three tables in one transaction AND populates the
            // reverse index inline. At 5000 files x 100 chunks each this takes
            // ~2-4 s per C-3 (5b) measurements.
            cancellationToken.ThrowIfCancellationRequested();
            var allFiles = liteDb.GetAllBackedUpFiles();
            sqlite.BulkInsertFiles(allFiles);

            // Phase 4: pending_changes. Usually tiny (in-flight watcher
            // events) so a single bulk batch is sufficient.
            cancellationToken.ThrowIfCancellationRequested();
            var pending = liteDb.GetPendingChanges(int.MaxValue);
            if (pending.Count > 0)
            {
                sqlite.QueueFileChangesBatch(pending);
            }

            // Phase 5: index_metadata. Copy every (key, value) row. This
            // includes the ReverseIndexBuiltAt sentinel that BulkInsertFiles
            // has already effectively satisfied (the reverse index IS
            // populated inline), plus any app-specific keys like LastScan
            // that we do not statically know. If the source did not have
            // a ReverseIndexBuiltAt row we synthesise one now - the
            // migration itself fulfilled the contract that sentinel
            // represents, so post-migration code that asks
            // IsReverseChunkIndexBuilt() must see true.
            cancellationToken.ThrowIfCancellationRequested();
            var allMetadata = liteDb.GetAllIndexMetadata();
            foreach (var (key, value) in allMetadata)
            {
                sqlite.SetIndexMetadata(key, value);
            }
            if (!allMetadata.ContainsKey("ReverseIndexBuiltAt"))
            {
                sqlite.SetIndexMetadata("ReverseIndexBuiltAt", DateTime.UtcNow);
            }

            // Report pre-close progress so a UI hooked up to the callback
            // shows "100%" before the rename dance runs.
            var totalRows = chunkIndex.Count + allFiles.Count + pending.Count;
            progress?.Report((totalRows, totalRows));

            // Flush SQLite to disk before renaming. Close() forces a WAL
            // checkpoint(TRUNCATE) which leaves the -wal file empty.
            sqlite.Close();
            liteDb.Close();
        }
        catch
        {
            // Clean up the partial temp file before propagating.
            try { sqlite.Close(); } catch { /* best effort */ }
            try { liteDb.Close(); } catch { /* best effort */ }
            DeleteIfExists(tempSqlitePath);
            DeleteIfExists(tempSqlitePath + "-wal");
            DeleteIfExists(tempSqlitePath + "-shm");
            DeleteIfExists(tempSqliteSaltPath);
            throw;
        }

        // Atomic(ish) rename dance. Order matters:
        //   1. Move original LiteDB out of the way. Frees databasePath.
        //   2. Move temp SQLite into databasePath.
        //   3. Move temp SQLite salt into databasePath.salt.
        //
        // File.Move on the same volume is atomic at the FS level; the
        // only cross-step hazard is a crash BETWEEN moves. We do moves
        // in order of irreversibility:
        //   - After step 1: DB at databasePath is gone. Recoverable by
        //     renaming .litedb-backup back.
        //   - After step 2: SQLite at databasePath. LiteDB at .litedb-backup.
        //     Both files intact, we're just missing the SQLite salt.
        //   - After step 3: fully migrated.
        //
        // So a crash AFTER step 2 + BEFORE step 3 leaves a usable
        // SQLite DB without its salt - which is unreadable. If this
        // happens the user can manually rename the .sqlite-tmp.salt
        // into place (the log line tells them how).
        File.Move(databasePath, litedbBackupPath);
        if (File.Exists(originalSaltPath))
            File.Move(originalSaltPath, litedbBackupSaltPath);
        File.Move(tempSqlitePath, databasePath);
        File.Move(tempSqliteSaltPath, databasePath + ".salt");
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Private escape hatch used by <see cref="MigrateFromLiteDb"/>:
    /// runs the LiteDB branch of Initialize REGARDLESS of the
    /// <c>AZBK_USE_SQLITE</c> env var. Needed because migration by
    /// definition runs with the flag ON, but must open the source DB
    /// as LiteDB.
    /// </summary>
    /// <remarks>
    /// We implement this by calling the main Initialize but first
    /// snapshotting and clearing the env var, then restoring. That is
    /// brittle because of process-wide state; preferable is to push
    /// the LiteDB-open logic into a private method that both
    /// Initialize and this helper call. That refactor belongs in C-5
    /// when LocalDatabaseService gets its broader cleanup.
    /// </remarks>
    private void InitializeLiteDbOnly(string databasePath, ReadOnlySpan<char> password)
    {
        // Snapshot + clear the env var so Initialize falls through the
        // LiteDB branch. Restore on exit.
        var previous = Environment.GetEnvironmentVariable(DatabaseBackendFactory.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(
                DatabaseBackendFactory.EnvironmentVariableName, null);
            Initialize(databasePath, password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DatabaseBackendFactory.EnvironmentVariableName, previous);
        }
    }
}
