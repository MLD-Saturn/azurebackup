using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P3: measures the bulk-delete path, i.e. the orphan-cleanup /
/// file-removal hot loop that repeatedly calls
/// <c>GetChunkEntriesForFile</c> for many files in a row.
///
/// <para>
/// Legacy path: N full scans, each O(total_chunks). This is the scenario
/// that motivated the reverse index - a user deleting 100 files from a
/// 100K-chunk backup pays a ~100x tax under the legacy path.
/// </para>
///
/// <para>
/// This benchmark calls <c>GetChunkEntriesForFile</c> rather than the full
/// <c>RemoveFileReferencesAsync</c> so the measurement stays focused on
/// the index-lookup axis; Azure blob deletion would dominate a full
/// end-to-end benchmark and is orthogonal to the Phase 5 change.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class BulkRemoveFileReferencesBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int TotalChunks = 100_000;
    private const int ChunksPerFile = 100;
    private const int FilesCount = 1_000;

    [Params(10, 100)]
    public int FilesToDelete { get; set; }

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
                        ChunkIndex = i % ChunksPerFile,
                        ReferencedAt = now
                    }
                ]
            });
        }
        _databaseService.BulkInsertChunkIndexEntries(entries);
        _databaseService.RebuildReverseChunkIndex();

        // Pick FilesToDelete targets spread across the file range.
        _targetFiles = new string[FilesToDelete];
        for (int i = 0; i < FilesToDelete; i++)
        {
            _targetFiles[i] = $"C:\\bench\\file-{i * FilesCount / FilesToDelete:D6}.bin";
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "Phase5: bulk reverse-index lookups")]
    public int Phase5_Bulk()
    {
        var total = 0;
        foreach (var file in _targetFiles)
        {
            total += _databaseService.GetChunkEntriesForFile(file).Count;
        }
        return total;
    }

    [Benchmark(Baseline = true, Description = "Legacy: bulk full-scan lookups")]
    public int Legacy_Bulk()
    {
        var total = 0;
        foreach (var file in _targetFiles)
        {
            total += _databaseService.GetChunkEntriesForFile_LegacyScan(file).Count;
        }
        return total;
    }
}
