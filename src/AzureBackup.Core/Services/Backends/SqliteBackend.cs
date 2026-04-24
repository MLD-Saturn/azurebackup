using System.Security.Cryptography;
using AzureBackup.Core.Models;
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
internal sealed partial class SqliteBackend : IDatabaseBackend
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
    /// Single-writer reader-writer lock used by every method that begins
    /// a SQLite transaction. <see cref="SqliteConnection"/> does not
    /// support nested transactions, so two concurrent writers on the
    /// same connection throw "SqliteConnection does not support nested
    /// transactions". Wrapping every write in this lock matches the
    /// LiteDB-side <c>InWriteLock</c> contract that
    /// <see cref="LocalDatabaseService"/>'s consumers (e.g.
    /// <c>BackupOrchestrator</c>'s parallel backup loop and
    /// <c>ChunkIndexService</c>) rely on.
    ///
    /// <para>
    /// <b>Recursion policy:</b> <see cref="LockRecursionPolicy.NoRecursion"/>.
    /// No public method on <see cref="SqliteBackend"/> takes the write
    /// lock and then re-enters another method that takes it.
    /// </para>
    ///
    /// <para>
    /// <b>B23 correction:</b> earlier comments here claimed "Reads are
    /// NOT lock-protected: SQLite WAL allows concurrent readers".
    /// That was wrong. WAL allows concurrent readers across DIFFERENT
    /// connections; with the single shared <see cref="SqliteConnection"/>
    /// this backend uses, two threads doing CreateCommand /
    /// ExecuteReader / Dispose on it concurrently produce
    /// <c>ArgumentOutOfRangeException</c> from inside Microsoft.Data.Sqlite's
    /// command tracker, AND any in-flight reader holds an implicit
    /// transaction that prevents a concurrent writer from issuing
    /// BEGIN -- producing <c>SQLite Error 1: 'cannot start a
    /// transaction within a transaction'</c>. Both shapes were observed
    /// in production telemetry. Reads now take a read lock via
    /// <see cref="InReadLock{T}"/>; writes continue through the
    /// write lock. The slim RWL allows readers to run concurrently
    /// with each other but mutually excludes readers from writers.
    /// </para>
    /// </summary>
    private readonly System.Threading.ReaderWriterLockSlim _writeLock
        = new(System.Threading.LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Helper that acquires <see cref="_writeLock"/>, runs the action,
    /// and releases the lock - including on exceptions. Centralises the
    /// try/finally pattern so individual writers stay readable.
    /// </summary>
    /// <remarks>
    /// B18: re-checks <see cref="_connection"/> AFTER acquiring the lock
    /// so a writer that passed its outer null-check before a racing
    /// <see cref="Close"/> tore the connection down sees a clean
    /// <see cref="InvalidOperationException"/> instead of a
    /// <see cref="NullReferenceException"/> on the first command.
    /// Without this guard the parallel backup loop
    /// (<c>BackupOrchestrator.RunBackupLoopAsync</c>) could deref a null
    /// connection if the user closed the database mid-backup.
    /// </remarks>
    private void InWriteLock(Action action)
    {
        _writeLock.EnterWriteLock();
        try
        {
            if (_connection == null)
                throw new InvalidOperationException("Backend was closed before this writer could run.");
            action();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Generic variant of <see cref="InWriteLock(Action)"/> for writers
    /// that need to return a value (e.g. row counts from DELETE).
    /// </summary>
    private T InWriteLock<T>(Func<T> action)
    {
        _writeLock.EnterWriteLock();
        try
        {
            if (_connection == null)
                throw new InvalidOperationException("Backend was closed before this writer could run.");
            return action();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// B23: read-side counterpart to <see cref="InWriteLock(Action)"/>.
    /// <para>
    /// Pre-B23 the backend's contract claimed reads were safe to run
    /// without lock protection because "SQLite WAL allows concurrent
    /// readers". That claim only holds for readers on DIFFERENT
    /// connections; this backend uses ONE shared
    /// <see cref="SqliteConnection"/>, and Microsoft.Data.Sqlite is
    /// explicit that a single connection is NOT thread-safe -- two
    /// threads doing CreateCommand/ExecuteReader/Dispose on the same
    /// connection corrupt M.D.Sqlite's internal command tracker
    /// (surfaces as <c>ArgumentOutOfRangeException</c> from inside the
    /// driver) AND any in-flight reader holds an implicit transaction
    /// that prevents a concurrent writer from issuing BEGIN
    /// (surfaces as <c>SQLite Error 1: 'cannot start a transaction
    /// within a transaction'</c>). Production telemetry recorded both
    /// shapes when the orchestrator's parallel backup loop drove
    /// <see cref="ChunkIndexService.AddReference"/> at 8-way fan-out
    /// against this backend.
    /// </para>
    /// <para>
    /// <b>Implementation note:</b> the helper is named <c>InReadLock</c>
    /// for caller clarity ("this is a read operation, not a write"),
    /// but under the hood it acquires the <em>write</em> lock. M.D.Sqlite
    /// cannot tolerate concurrent access of any kind on a single
    /// <see cref="SqliteConnection"/> -- even reader-vs-reader is
    /// unsafe (a regression test confirmed two reader threads racing
    /// CreateCommand corrupt the internal command-tracker list). The
    /// underlying <see cref="System.Threading.ReaderWriterLockSlim"/>
    /// would gladly let two readers in concurrently, so we must use
    /// the exclusive write side for reads as well. The semantic name
    /// is preserved so call sites still document intent.
    /// </para>
    /// </summary>
    private T InReadLock<T>(Func<T> action)
    {
        _writeLock.EnterWriteLock();
        try
        {
            if (_connection == null)
                throw new InvalidOperationException("Backend was closed before this reader could run.");
            return action();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Salt file lives next to the database, identical convention to the
    /// LiteDB backend so an upgrading user's existing salt continues to work.
    /// </summary>
    private static string GetSaltFilePath(string databasePath) => databasePath + ".salt";

    public bool IsInitialized => _connection != null;
    public string? DatabasePath => _databasePath;

    /// <summary>
    /// B13: diagnostic-event surface so the backend can ship structured
    /// progress / error context to the file logger via
    /// <see cref="LocalDatabaseService"/>'s existing relay. Pre-B13 the
    /// Argon2id key derivation was completely silent in the log file;
    /// an OutOfMemoryException on a tester machine surfaced only as the
    /// generic UI-pane "Unlock failed" message with no diagnostic
    /// breadcrumbs to triage from. Now we emit timing + memory snapshots
    /// at every KDF entry/exit and log the OOM stack with pre/post
    /// LOH-compaction process state.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    /// <summary>
    /// B22: emit a diagnostic message to the connected sink. Marked
    /// <see cref="System.Diagnostics.ConditionalAttribute"/> on
    /// <c>DIAGNOSTICLOG</c> so the call site, the string interpolation,
    /// and the event invocation are ALL compiled away in Release
    /// builds -- matching the peer pattern in
    /// <c>LocalDatabaseService.Log</c>,
    /// <c>EncryptionService.Log</c>, etc. Pre-B22 this method ran
    /// unconditionally even in Release, violating the B14 contract
    /// that DIAGNOSTICLOG-disabled builds emit no logging of any kind.
    /// </summary>
    [System.Diagnostics.Conditional("DIAGNOSTICLOG")]
    private void EmitDiag(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [SqliteBackend] {message}");
    }

    /// <summary>
    /// B22: emit a diagnostic message to BOTH the connected sink AND the
    /// ambient <see cref="FileOperationDiagnostics"/> bundle (if one is
    /// active). The latter is the per-file <c>.diag</c> file that
    /// <c>BackupOrchestrator.BackupFileAsync</c> opens via
    /// <see cref="FileOperationDiagnostics.SetAmbient"/> -- backend-side
    /// failures during a parallel backup loop now land in that file's
    /// own <c>.diag</c> bundle automatically, instead of only in the
    /// global session log.
    /// <para>
    /// Use this for events that name a specific file path or chunk
    /// hash so the per-file forensic record stays complete. Use the
    /// plain <see cref="EmitDiag"/> for events that have no file
    /// scope (open / close / schema migration).
    /// </para>
    /// </summary>
    [System.Diagnostics.Conditional("DIAGNOSTICLOG")]
    private void EmitDiagAmbient(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var formatted = $"[{timestamp}] [SqliteBackend] {message}";
        DiagnosticLog?.Invoke(this, formatted);
        FileOperationDiagnostics.RecordAmbient(formatted);
    }

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
        EmitDiag($"Initialize: starting (path={databasePath})");

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // B10: snapshot DB existence BEFORE LoadOrCreateSalt; the salt
        // file write is a side effect we tolerate (we wrap it in a try/
        // catch + delete-on-failure below) but creating an empty
        // backup.db on a wrong-password attempt is not. The previous
        // SqliteOpenMode.ReadWriteCreate default would silently create
        // an empty SQLite file the moment connection.Open() ran, BEFORE
        // any key validation -- which then fooled subsequent app
        // launches into showing the unlock prompt instead of first-run
        // setup. Real bug observed by tester.
        var dbExistedBeforeOpen = File.Exists(databasePath);
        var saltExistedBeforeOpen = File.Exists(GetSaltFilePath(databasePath));
        EmitDiag($"Initialize: dbExistedBeforeOpen={dbExistedBeforeOpen}, saltExistedBeforeOpen={saltExistedBeforeOpen}");

        var salt = LoadOrCreateSalt(databasePath);
        var derivedKey = DeriveKeyFromPassword(password, salt);
        try
        {
            OpenAndUnlock(databasePath, derivedKey, dbExistedBeforeOpen);
            ApplyPragmas();
            CreateSchema();
            EmitDiag("Initialize: completed successfully");
        }
        catch (Exception ex)
        {
            EmitDiag($"Initialize: failed with {ex.GetType().Name}: {ex.Message}");
            // B10: clean up the salt file we just wrote if (a) the DB
            // didn't exist before AND (b) we failed to initialise. This
            // prevents the side-effect chain where a wrong-password on a
            // fresh install left a .salt file behind that then mis-paired
            // with a future legitimate backup.db. Files we did NOT just
            // write are left alone.
            if (!saltExistedBeforeOpen)
            {
                try { File.Delete(GetSaltFilePath(databasePath)); } catch { /* best effort */ }
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Forces any deferred writes (WAL pages) to be persisted into the main
    /// database file. Idempotent. Safe to call from any thread under the
    /// LocalDatabaseService write lock.
    /// </summary>
    public void Checkpoint()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }


    // B22: large method bodies extracted to sibling partial files.
    // See SqliteBackend.Schema.cs (open + KDF + schema),
    //     SqliteBackend.IndexMetadata.cs,
    //     SqliteBackend.Configuration.cs,
    //     SqliteBackend.Files.cs,
    //     SqliteBackend.ChunkIndex.cs,
    //     SqliteBackend.ChunkFileRefs.cs,
    //     SqliteBackend.PendingChanges.cs,
    //     SqliteBackend.Statistics.cs,
    //     SqliteBackend.IntegrityCheck.cs,
    //     SqliteBackend.TestHooks.cs.
    // This file owns the lifecycle surface (init / close / dispose /
    // secure reset / ambient diag wiring); each partial owns one
    // responsibility and stays under ~550 lines.

    public void Close()
    {
        // B22: diag breadcrumb so the session log records every close
        // attempt -- including the rare "lock already disposed" branch
        // that pre-B22 ran completely silently.
        EmitDiag("Close: enter");

        // B18: serialize against in-flight writers. Pre-B18 a user-driven
        // Close (e.g. clicking "Lock app" or shutting down) could race
        // the parallel backup loop's SaveBackedUpFile / SaveChunkIndexEntry
        // workers: the worker passed its outer (_connection == null) check
        // and was about to call _connection.BeginTransaction() when this
        // method's _connection.Dispose() ran -- producing an opaque
        // NullReferenceException or ObjectDisposedException with no
        // diagnostic context. Now the worker either finishes its
        // transaction first, or sees the cleaned-up _connection through
        // InWriteLock's post-lock re-check and surfaces a typed
        // InvalidOperationException("Backend was closed before this
        // writer could run.").
        //
        // Two safety guarantees this method preserves:
        // 1. Never throws (the catch wraps everything; pre-B18 contract).
        // 2. Idempotent (safe to call multiple times; second call is a no-op).
        try
        {
            // EnterWriteLock itself can throw ObjectDisposedException
            // if the caller has already Disposed the backend (legal under
            // a paranoid double-dispose). In that case we have nothing to
            // checkpoint anyway -- _connection is null on the second call.
            _writeLock.EnterWriteLock();
        }
        catch
        {
            // Lock unavailable (already disposed). Fall through to the
            // best-effort dispose path; if _connection is also already
            // null we'll just exit.
            EmitDiag("Close: write lock unavailable (already disposed); falling through to lock-bypass close");
            CloseConnectionInternal();
            return;
        }

        try
        {
            CloseConnectionInternal();
        }
        finally
        {
            try { _writeLock.ExitWriteLock(); } catch { /* lock already gone */ }
        }
        EmitDiag("Close: exit");
    }

    /// <summary>
    /// B18: shared body for <see cref="Close"/>. Assumes the caller
    /// holds the write lock (or has decided proceeding without it is
    /// safe because the lock is already disposed). Best-effort
    /// checkpoint + dispose; never throws.
    /// </summary>
    private void CloseConnectionInternal()
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
                EmitDiag("CloseConnectionInternal: WAL checkpoint OK");
            }
            catch (Exception ex)
            {
                // Best-effort; never throw from Close. Diag is gated on
                // DIAGNOSTICLOG so the catch body is empty in Release
                // (matches the pre-B22 contract).
                EmitDiag($"CloseConnectionInternal: checkpoint failed (best-effort) -- {ex.GetType().Name}: {ex.Message}");
            }

            try { _connection.Dispose(); } catch (Exception ex) { EmitDiag($"CloseConnectionInternal: connection.Dispose threw -- {ex.GetType().Name}: {ex.Message}"); }
            _connection = null;
        }
    }

    public void Dispose()
    {
        Close();
        // Dispose the lock once; harmless if Dispose was called more than
        // once because ReaderWriterLockSlim.Dispose is idempotent.
        try { _writeLock.Dispose(); } catch { /* terminal best-effort */ }
    }

    /// <summary>
    /// Zeroes sensitive config fields, closes the connection, and deletes
    /// every on-disk artefact belonging to this backend's database: the
    /// main DB file, the -wal and -shm companion files SQLite may have
    /// left behind, and the salt file produced by
    /// <see cref="OpenAndUnlockCore"/>.
    /// </summary>
    /// <remarks>
    /// The overwrite+delete pattern mirrors
    /// <c>LocalDatabaseService.SecureReset</c> semantics so callers that
    /// switch backends see the same post-condition. Three-pass overwriting
    /// is intentionally NOT done here - SQLCipher pages are already
    /// encrypted at rest so raw-data remanence is not a concern; the goal
    /// is just "file gone" from the user's perspective.
    /// </remarks>
    public void SecureReset()
    {
        var path = _databasePath;
        EmitDiag($"SecureReset: enter (path={path ?? "(null)"})");

        // Zero sensitive config BEFORE closing, so the final checkpoint
        // writes the scrubbed page to disk before we delete the file.
        if (_connection != null && !string.IsNullOrEmpty(path))
        {
            try
            {
                InWriteLock(() =>
                {
                    using var cmd = _connection.CreateCommand();
                    // The config table holds encrypted_connection_string,
                    // password_salt, and password_verification_hash - the
                    // three fields LocalDatabaseService overwrites in
                    // OverwriteSensitiveData. NULL them out in one statement.
                    cmd.CommandText = """
                        UPDATE config
                        SET encrypted_connection_string = NULL,
                            password_salt = NULL,
                            password_verification_hash = NULL
                        WHERE id = 1;
                        """;
                    cmd.ExecuteNonQuery();
                });
                EmitDiag("SecureReset: scrubbed sensitive config columns");
            }
            catch (Exception ex)
            {
                // Best-effort; the subsequent file deletion renders the
                // scrubbing moot, but we try first so a filesystem error
                // still leaves the DB with scrubbed config.
                EmitDiag($"SecureReset: scrub UPDATE failed (best-effort) -- {ex.GetType().Name}: {ex.Message}");
            }
        }

        Close();

        if (string.IsNullOrEmpty(path))
        {
            EmitDiag("SecureReset: no path recorded; file deletion skipped");
            return;
        }

        // Securely delete every artefact SQLite (and our salt-file
        // convention) might have written. The DB file + salt carry
        // key material so we route through TrySecureDelete (single
        // random-bytes pass + Flush(true) + unlink). The WAL/SHM/journal
        // are also derived from encrypted page content; if SQLCipher is
        // active they're already ciphertext, but we still overwrite for
        // defence in depth on hosts that may have run with a fallback
        // unencrypted SQLite build. Same helper used by
        // LocalDatabaseService.SecureReset on the LiteDB side.
        DeleteAndDiag(path);
        DeleteAndDiag(path + "-wal");
        DeleteAndDiag(path + "-shm");
        DeleteAndDiag(path + "-journal");
        DeleteAndDiag(GetSaltFilePath(path));

        _databasePath = null;
        EmitDiag("SecureReset: completed");
    }

    /// <summary>
    /// B22: TrySecureDelete wrapper that records the outcome via diag.
    /// Pre-B22 a delete that failed (file in use by antivirus, locked
    /// by Explorer indexer, etc.) gave no signal to the operator.
    /// </summary>
    private void DeleteAndDiag(string filePath)
    {
        var existedBefore = File.Exists(filePath);
        FileSystemHelper.TrySecureDelete(filePath);
        var stillThere = File.Exists(filePath);
        if (existedBefore && stillThere)
        {
            EmitDiag($"SecureReset: TrySecureDelete failed to remove {filePath} -- file still present after best-effort attempt");
        }
        else if (existedBefore)
        {
            EmitDiag($"SecureReset: deleted {filePath}");
        }
    }

}
