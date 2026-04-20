using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// <see cref="IDatabaseBackend"/> implementation that adapts the existing
/// <see cref="LocalDatabaseService"/> by pure delegation. Exists so we can
/// write contract tests and benchmarks that target the LiteDB and SQLite
/// backends through the same API, without disturbing the 26 consumers that
/// currently construct <see cref="LocalDatabaseService"/> directly.
///
/// <para>
/// <b>Design choice.</b> A wrapper class rather than making
/// <see cref="LocalDatabaseService"/> itself implement
/// <see cref="IDatabaseBackend"/>. The interface is internal; the service
/// is public; mixing those visibilities forces explicit-interface
/// implementation that makes every method ugly to call from production
/// code. The wrapper costs one extra virtual call per operation - well
/// below measurable noise for any operation we care about.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b> The adapter constructs and owns a fresh
/// <see cref="LocalDatabaseService"/> instance. Disposing the adapter
/// disposes the underlying service. Callers must NOT supply an external
/// service to wrap; the ownership semantics get confusing fast.
/// </para>
///
/// <para>
/// <b>Behavioural equivalence with SqliteBackend</b> is verified by the
/// shared contract tests in <c>BackendContractTests</c>. Where the two
/// backends diverge intentionally (e.g. <c>GetChunkIndexEntry</c> always
/// returns an empty <c>ReferencingFiles</c> list on SQLite but populates
/// it on LiteDB), the contract tests assert only the common shape.
/// </para>
/// </summary>
internal sealed class LiteDbBackend : IDatabaseBackend
{
    private readonly LocalDatabaseService _service;
    private bool _disposed;

    public LiteDbBackend()
    {
        _service = new LocalDatabaseService();
    }

    public bool IsInitialized => _service.IsInitialized;

    public string? DatabasePath => _service.DatabasePath;

    public void Initialize(string databasePath, ReadOnlySpan<char> password)
        => _service.Initialize(databasePath, password);

    public void Checkpoint() => _service.Checkpoint();

    // ---- IndexMetadata ------------------------------------------------------

    public DateTime? GetIndexMetadata(string key) => _service.GetIndexMetadata(key);

    public void SetIndexMetadata(string key, DateTime value)
        => _service.SetIndexMetadata(key, value);

    // ---- Configuration ------------------------------------------------------

    public BackupConfiguration GetConfiguration() => _service.GetConfiguration();

    public void SaveConfiguration(BackupConfiguration configuration)
        => _service.SaveConfiguration(configuration);

    // ---- BackedUpFile -------------------------------------------------------

    public BackedUpFile? GetBackedUpFile(string localPath)
        => _service.GetBackedUpFile(localPath);

    public void SaveBackedUpFile(BackedUpFile file) => _service.SaveBackedUpFile(file);

    public List<BackedUpFile> GetAllBackedUpFiles() => _service.GetAllBackedUpFiles();

    // ---- ChunkIndex ---------------------------------------------------------

    public ChunkIndexEntry? GetChunkIndexEntry(string chunkHash)
        => _service.GetChunkIndexEntry(chunkHash);

    public void SaveChunkIndexEntry(ChunkIndexEntry entry)
        => _service.SaveChunkIndexEntry(entry);

    public void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries)
        => _service.BulkInsertChunkIndexEntries(entries);

    public void DeleteChunkIndexEntry(string chunkHash)
        => _service.DeleteChunkIndexEntry(chunkHash);

    public List<ChunkIndexEntry> GetAllChunkIndexEntries()
        => _service.GetAllChunkIndexEntries();

    public Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)>
        GetChunkIndexSummaryMap() => _service.GetChunkIndexSummaryMap();

    public int GetChunkIndexCount() => _service.GetChunkIndexCount();

    public void ClearChunkIndex() => _service.ClearChunkIndex();

    public List<ChunkIndexEntry> GetOrphanedChunks() => _service.GetOrphanedChunks();

    // ---- Reverse chunk index -----------------------------------------------

    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath)
        => _service.GetChunkEntriesForFile(filePath);

    public bool IsReverseChunkIndexBuilt() => _service.IsReverseChunkIndexBuilt();

    public void RebuildReverseChunkIndex(
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
        => _service.RebuildReverseChunkIndex(progress, cancellationToken);

    // ---- Pending changes queue ---------------------------------------------

    public void QueueFileChange(FileChangeEvent change) => _service.QueueFileChange(change);

    public void QueueFileChangesBatch(IEnumerable<FileChangeEvent> changes)
        => _service.QueueFileChangesBatch(changes);

    public List<FileChangeEvent> GetPendingChanges(int batchSize = 100)
        => _service.GetPendingChanges(batchSize);

    public void RemovePendingChange(string filePath) => _service.RemovePendingChange(filePath);

    public HashSet<string> GetAllPendingChangePaths() => _service.GetAllPendingChangePaths();

    // ---- Aggregate statistics ----------------------------------------------

    public BackupStatistics GetStatistics() => _service.GetStatistics();

    // ---- Lifecycle ---------------------------------------------------------

    /// <summary>
    /// Closes the underlying <see cref="LocalDatabaseService"/> connection
    /// without disposing the wrapper - leaves the backend reinitialisable.
    /// Matches the <see cref="IDatabaseBackend.Close"/> contract.
    /// </summary>
    public void Close() => _service.Close();

    /// <summary>
    /// Delegates to <see cref="LocalDatabaseService.SecureReset"/>, which
    /// zeroes sensitive config, closes the LiteDB handle, and deletes the
    /// db / journal / log / salt files.
    /// </summary>
    public void SecureReset() => _service.SecureReset();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Dispose();
    }
}
