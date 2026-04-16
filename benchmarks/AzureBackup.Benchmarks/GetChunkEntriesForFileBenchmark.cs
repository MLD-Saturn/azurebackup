using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P3: compares the legacy full-scan implementation of
/// <c>GetChunkEntriesForFile</c> against the reverse-index lookup added in
/// Phase 5.
///
/// <para>
/// The legacy path is O(total_chunks) because LiteDB cannot index into the
/// nested <c>ReferencingFiles</c> list, so every row is loaded and filtered
/// in memory. The Phase 5 path does an indexed <c>FilePath</c> lookup on
/// <c>chunk_file_refs</c> then fetches each matching entry by its indexed
/// <c>ChunkHash</c>, which is O(chunks_for_this_file).
/// </para>
/// </summary>
[MemoryDiagnoser]
public class GetChunkEntriesForFileBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int FilesCount = 500;
    private const int ChunksPerFile = 10;

    [Params(10_000, 50_000)]
    public int TotalChunks { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LocalDatabaseService _databaseService = null!;
    private string _targetFile = string.Empty;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, Password);

        // Seed TotalChunks entries across FilesCount files. Each chunk is
        // referenced by exactly one file; every ChunksPerFile-th chunk belongs
        // to a file name derived from the sequence.
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        for (int i = 0; i < TotalChunks; i++)
        {
            var fileIndex = i % FilesCount;
            var chunkIndex = i / FilesCount;
            entries.Add(new ChunkIndexEntry
            {
                ChunkHash = HashString(i),
                FirstUploadedAt = now,
                SizeBytes = 65_536,
                ReferenceCount = 1,
                ReferencingFiles =
                [
                    new ChunkFileReference
                    {
                        FilePath = $"C:\\bench\\file-{fileIndex:D4}.bin",
                        ChunkIndex = chunkIndex,
                        ReferencedAt = now
                    }
                ]
            });
        }
        _databaseService.BulkInsertChunkIndexEntries(entries);

        // Build the reverse index for the Phase 5 path.
        _databaseService.RebuildReverseChunkIndex();

        _targetFile = "C:\\bench\\file-0042.bin";
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Phase 5 fast path: indexed reverse lookup.
    /// </summary>
    [Benchmark(Description = "Phase5: reverse-index lookup")]
    public int Phase5_ReverseIndex()
    {
        return _databaseService.GetChunkEntriesForFile(_targetFile).Count;
    }

    /// <summary>
    /// Legacy baseline: full scan over chunk_index + in-memory filter.
    /// Exposed via the internal legacy accessor so the benchmark keeps both
    /// code paths live for apples-to-apples comparison.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Legacy: full chunk-index scan")]
    public int Legacy_FullScan()
    {
        return _databaseService.GetChunkEntriesForFile_LegacyScan(_targetFile).Count;
    }

    private static string HashString(int seed)
    {
        // Deterministic 64-char hex derived from the seed; not cryptographic.
        Span<byte> bytes = stackalloc byte[32];
        BitConverter.TryWriteBytes(bytes, seed);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
