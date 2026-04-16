using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P2+P3 interaction: runs many parallel
/// <c>GetChunkEntriesForFile</c> calls from different threads. The legacy
/// path serialises behind the read lock for the full scan duration; the
/// reverse path releases the lock quickly, so concurrent callers overlap.
///
/// <para>
/// This models the real hotspot that motivated Phase 5: multiple UI panes
/// (file list, storage health, sync view) all query chunk-to-file
/// relationships simultaneously while the backup loop reads and writes.
/// Under the coarse monitor every access serialised; under the RWLock the
/// reverse path lets readers truly run in parallel.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ConcurrentGetChunkEntriesBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int TotalChunks = 100_000;
    private const int ChunksPerFile = 100;
    private const int FilesCount = 1_000;

    /// <summary>
    /// Number of threads calling <c>GetChunkEntriesForFile</c> at once.
    /// </summary>
    [Params(1, 4, 16)]
    public int ConcurrentReaders { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LocalDatabaseService _databaseService = null!;
    private string[] _targetFiles = Array.Empty<string>();

    [GlobalSetup]
    public void GlobalSetup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, Password);

        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        for (int i = 0; i < TotalChunks; i++)
        {
            var fileIndex = i / ChunksPerFile % FilesCount;
            var chunkIndex = i % ChunksPerFile;
            entries.Add(new ChunkIndexEntry
            {
                ChunkHash = BenchDataHelper.HashString(i),
                FirstUploadedAt = now,
                SizeBytes = 65_536,
                ReferenceCount = 1,
                ReferencingFiles =
                [
                    new ChunkFileReference
                    {
                        FilePath = $"C:\\bench\\file-{fileIndex:D6}.bin",
                        ChunkIndex = chunkIndex,
                        ReferencedAt = now
                    }
                ]
            });
        }
        _databaseService.BulkInsertChunkIndexEntries(entries);
        _databaseService.RebuildReverseChunkIndex();

        // Each reader queries a different file so the DB pages touched spread
        // across the index (avoids cache-hit bias where every reader sees the
        // same hot page).
        _targetFiles = new string[ConcurrentReaders];
        for (int i = 0; i < ConcurrentReaders; i++)
        {
            _targetFiles[i] = $"C:\\bench\\file-{i * 17 % FilesCount:D6}.bin";
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "Phase5: concurrent reverse-index readers")]
    public int Phase5_Concurrent()
    {
        var tasks = new Task<int>[ConcurrentReaders];
        for (int i = 0; i < ConcurrentReaders; i++)
        {
            var file = _targetFiles[i];
            tasks[i] = Task.Run(() => _databaseService.GetChunkEntriesForFile(file).Count);
        }
        Task.WaitAll(tasks);
        return tasks[0].Result;
    }

    [Benchmark(Baseline = true, Description = "Legacy: concurrent full-scan readers")]
    public int Legacy_Concurrent()
    {
        var tasks = new Task<int>[ConcurrentReaders];
        for (int i = 0; i < ConcurrentReaders; i++)
        {
            var file = _targetFiles[i];
            tasks[i] = Task.Run(() => _databaseService.GetChunkEntriesForFile_LegacyScan(file).Count);
        }
        Task.WaitAll(tasks);
        return tasks[0].Result;
    }
}
