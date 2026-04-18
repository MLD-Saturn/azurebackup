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

    // ---- Configuration ------------------------------------------------------

    /// <summary>
    /// Returns the singleton <see cref="BackupConfiguration"/> row, or a new
    /// default-constructed instance when no configuration has been saved yet.
    /// Always returns a non-null result so callers do not need null checks.
    /// </summary>
    BackupConfiguration GetConfiguration();

    /// <summary>
    /// Upserts the singleton <see cref="BackupConfiguration"/> row plus its
    /// nested watched-folder and global-exclude lists. Performed under a
    /// single transaction so partial writes can never be observed.
    /// </summary>
    void SaveConfiguration(BackupConfiguration configuration);

    // ---- BackedUpFile -------------------------------------------------------

    /// <summary>
    /// Looks up a single backed-up file by its local path. Returns
    /// <c>null</c> when no row matches. The returned object includes its
    /// full <see cref="BackedUpFile.Chunks"/> list in original index order.
    /// </summary>
    BackedUpFile? GetBackedUpFile(string localPath);

    /// <summary>
    /// Inserts or updates a backed-up-file record by <see cref="BackedUpFile.LocalPath"/>.
    /// The nested chunk list is replaced atomically: any existing chunks
    /// for the same row are deleted and the new set inserted in a single
    /// transaction so readers never observe a partial chunk list.
    /// </summary>
    void SaveBackedUpFile(BackedUpFile file);

    /// <summary>
    /// Returns every backed-up-file row in the database, each populated
    /// with its full <see cref="BackedUpFile.Chunks"/> list. Order across
    /// files is not specified.
    /// </summary>
    List<BackedUpFile> GetAllBackedUpFiles();

    // ---- ChunkIndex ---------------------------------------------------------

    /// <summary>
    /// Looks up a single <see cref="ChunkIndexEntry"/> by its
    /// <see cref="ChunkIndexEntry.ChunkHash"/>, or <c>null</c> if no row matches.
    /// The returned object's <see cref="ChunkIndexEntry.ReferencingFiles"/>
    /// list is left empty - callers needing the reverse-index data should
    /// query the dedicated reverse-index path (added in C-1e-2).
    /// </summary>
    ChunkIndexEntry? GetChunkIndexEntry(string chunkHash);

    /// <summary>
    /// Inserts or updates a single <see cref="ChunkIndexEntry"/> row keyed
    /// by <see cref="ChunkIndexEntry.ChunkHash"/>.
    /// </summary>
    void SaveChunkIndexEntry(ChunkIndexEntry entry);

    /// <summary>
    /// Bulk-inserts many <see cref="ChunkIndexEntry"/> rows in a single
    /// transaction. Existing rows with matching <c>chunk_hash</c> are
    /// updated; this matches LiteDB's <c>InsertBulk</c> contract used at
    /// reverse-index rebuild time.
    /// </summary>
    void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries);

    /// <summary>
    /// Deletes a single chunk row by its hash. No-op if the row does not exist.
    /// </summary>
    void DeleteChunkIndexEntry(string chunkHash);

    /// <summary>
    /// Returns every chunk row. Used by orphan scans, integrity checks,
    /// and reverse-index rebuild.
    /// </summary>
    List<ChunkIndexEntry> GetAllChunkIndexEntries();

    /// <summary>
    /// Returns every chunk row reduced to its (refcount, size, tier) tuple.
    /// Avoids materialising full <see cref="ChunkIndexEntry"/> instances on
    /// the hot statistics-summary path.
    /// </summary>
    Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)>
        GetChunkIndexSummaryMap();

    /// <summary>
    /// Returns the count of rows in <c>chunk_index</c>. Cheap (uses
    /// SQLite's row-count estimator on indexed tables).
    /// </summary>
    int GetChunkIndexCount();

    /// <summary>
    /// Removes every row from both <c>chunk_index</c> and
    /// <c>chunk_file_refs</c>. Used by integration tests and the
    /// "reset chunk index" maintenance command.
    /// </summary>
    void ClearChunkIndex();

    /// <summary>
    /// Returns every chunk whose <see cref="ChunkIndexEntry.ReferenceCount"/>
    /// is zero - candidates for deletion in an orphan-cleanup pass.
    /// </summary>
    List<ChunkIndexEntry> GetOrphanedChunks();

    // ---- Reverse chunk index (chunk_file_refs) -----------------------------

    /// <summary>
    /// Returns every <see cref="ChunkIndexEntry"/> referenced by
    /// <paramref name="filePath"/>. Implementations are expected to use
    /// the indexed reverse path (chunk_file_refs joined with chunk_index)
    /// rather than scanning the primary chunk index.
    /// </summary>
    /// <remarks>
    /// In the LiteDB backend this method went through several iterations
    /// (see Phase 5 / P3 and the b0c9439 regression). The SQLite backend
    /// uses a single SELECT JOIN with WHERE file_path = ? - clean SQL,
    /// no expression-tree limitations.
    /// </remarks>
    List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath);

    /// <summary>
    /// Returns true once <see cref="RebuildReverseChunkIndex"/> has
    /// completed at least once for this database. Detected via a
    /// dedicated <c>index_metadata</c> sentinel key so the check is O(1).
    /// </summary>
    bool IsReverseChunkIndexBuilt();

    /// <summary>
    /// Populates the reverse index for any backed-up files that lack
    /// matching <c>chunk_file_refs</c> rows. Idempotent. Used at
    /// migration time when a LiteDB-era database is opened by SQLite for
    /// the first time and again as a maintenance command.
    /// </summary>
    void RebuildReverseChunkIndex(
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default);

    // ---- Pending changes queue ---------------------------------------------

    /// <summary>
    /// Inserts a single change into the pending queue, replacing any
    /// existing pending entry that targets the same
    /// <see cref="FileChangeEvent.FilePath"/>. The replace-then-insert pair
    /// runs inside one transaction.
    /// </summary>
    void QueueFileChange(FileChangeEvent change);

    /// <summary>
    /// Bulk variant of <see cref="QueueFileChange"/>: deduplicates the input
    /// by <see cref="FileChangeEvent.FilePath"/> (last-write-wins) and
    /// performs all DELETE + INSERT work inside a single transaction. A
    /// null or empty input sequence is a no-op.
    /// </summary>
    void QueueFileChangesBatch(IEnumerable<FileChangeEvent> changes);

    /// <summary>
    /// Returns the next batch of pending changes ordered by
    /// <see cref="FileChangeEvent.DetectedAt"/> ascending. <paramref name="batchSize"/>
    /// values <= 0 are treated as the default of 100.
    /// </summary>
    List<FileChangeEvent> GetPendingChanges(int batchSize = 100);

    /// <summary>
    /// Removes every pending row whose <see cref="FileChangeEvent.FilePath"/>
    /// matches <paramref name="filePath"/>. No-op if no rows match.
    /// </summary>
    void RemovePendingChange(string filePath);

    /// <summary>
    /// Returns every pending file path as an ordinal-ignore-case set so
    /// callers can answer "is X pending?" without one round-trip per check.
    /// </summary>
    HashSet<string> GetAllPendingChangePaths();

    // ---- Aggregate statistics ----------------------------------------------

    /// <summary>
    /// Returns aggregate counts and sizes across the files, pending_changes,
    /// and config tables in a single round-trip-friendly call. Used by the
    /// status pane and by maintenance commands that need to display counts
    /// without materialising row collections.
    /// </summary>
    BackupStatistics GetStatistics();
}
