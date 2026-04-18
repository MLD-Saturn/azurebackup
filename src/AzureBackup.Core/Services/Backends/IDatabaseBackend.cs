using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// Internal abstraction over the persistence layer used by
/// <see cref="LocalDatabaseService"/>. Lets us run the same logical
/// surface against either LiteDB (legacy) or SQLite + SQLCipher (Option C).
///
/// <para>
/// <b>Threading contract.</b> Implementations are not required to be
/// thread-safe on their own. <see cref="LocalDatabaseService"/> serialises
/// access through its <see cref="System.Threading.ReaderWriterLockSlim"/>;
/// the backend may assume single-threaded access per call. Implementations
/// MAY choose finer-grained internal locking for performance but must not
/// rely on it for correctness.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b> <see cref="Initialize"/> opens (or creates) the store.
/// Every other method requires the backend to be initialised; calling them
/// before <see cref="Initialize"/> throws <see cref="InvalidOperationException"/>.
/// <see cref="System.IDisposable.Dispose"/> closes the store.
/// </para>
///
/// <para>
/// <b>This interface is intentionally empty for now.</b> Methods are added
/// in C-1c onwards as each table is implemented in <c>SqliteBackend</c>;
/// keeping the surface small until both backends agree on each signature
/// avoids a stop-the-world refactor of <see cref="LocalDatabaseService"/>.
/// </para>
/// </summary>
internal interface IDatabaseBackend : IDisposable
{
    /// <summary>
    /// Opens or creates the encrypted database at <paramref name="databasePath"/>
    /// with a key derived from <paramref name="password"/> via Argon2id.
    /// </summary>
    void Initialize(string databasePath, ReadOnlySpan<char> password);

    /// <summary>
    /// True after <see cref="Initialize"/> has succeeded, false after
    /// <see cref="System.IDisposable.Dispose"/>.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Returns the on-disk path of the open database, or <c>null</c> when
    /// not initialised.
    /// </summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Forces any deferred writes to be persisted. For SQLite this runs
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>; for LiteDB it calls the
    /// engine's checkpoint API. Idempotent.
    /// </summary>
    void Checkpoint();

    // ---- IndexMetadata ------------------------------------------------------

    /// <summary>
    /// Reads a timestamp value previously stored via
    /// <see cref="SetIndexMetadata"/>, or <c>null</c> if no value exists for
    /// <paramref name="key"/>. Used by Phase 5 to record reverse-index build
    /// timestamps and similar housekeeping.
    /// </summary>
    DateTime? GetIndexMetadata(string key);

    /// <summary>
    /// Writes a timestamp value associated with <paramref name="key"/>,
    /// overwriting any previous value.
    /// </summary>
    void SetIndexMetadata(string key, DateTime value);
}
