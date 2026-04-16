using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P3: compares the legacy full-scan implementation of
/// <c>GetChunkEntriesForFile</c> against the reverse-index lookup.
///
/// <para>
/// The legacy path is O(total_chunks) because LiteDB cannot index into the
/// nested <c>ReferencingFiles</c> list, so every row is loaded and filtered
/// in memory. The Phase 5 path does an indexed <c>FilePath</c> lookup on
/// <c>chunk_file_refs</c> then fetches each matching entry by its indexed
/// <c>ChunkHash</c>, which is O(chunks_for_this_file).
/// </para>
///
/// <para>
/// Parameterised over <see cref="TotalChunks"/> (database size) and
/// <see cref="ChunksPerFile"/> (chunks referenced by the target file) so the
/// benchmark shows:
/// </para>
/// <list type="bullet">
///   <item>Worst case for Phase 5 (few chunks per file, small DB) - legacy
///     can win on wall-clock time because sequential FindAll beats many
///     indexed seeks - but Phase 5 still wins on allocations.</item>
///   <item>Realistic case (100 chunks per file, mid-size DB) - the paths
///     converge; Phase 5 begins to lead on both axes.</item>
///   <item>Best case (1000 chunks per file, 500K DB) - Phase 5 dominates
///     because legacy has to deserialise half a million rows just to find
///     the referenced ones.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class GetChunkEntriesForFileBenchmark
{
    private const string Password = "BenchmarkPassword123!";

    /// <summary>
    /// Database-wide chunk count. 500 000 is representative of a ~500 GB
    /// backup at the default 1 MB target chunk size.
    /// </summary>
    [Params(10_000, 100_000, 500_000)]
    public int TotalChunks { get; set; }

    /// <summary>
    /// Number of chunks referenced by the target file. Controls the size of
    /// the result set - independent of database size, which is the whole
    /// point of the reverse index.
    /// </summary>
    [Params(10, 100, 1_000)]
    public int ChunksPerFile { get; set; }

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

        // First ChunksPerFile chunks belong to the target file; the rest are
        // spread across ~(TotalChunks-ChunksPerFile)/10 other files so the
        // reverse index contains realistic variety.
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        var otherFileCount = Math.Max(1, (TotalChunks - ChunksPerFile) / 10);

        for (int i = 0; i < TotalChunks; i++)
        {
            string filePath;
            int chunkIndex;
            if (i < ChunksPerFile)
            {
                filePath = "C:\\bench\\target.bin";
                chunkIndex = i;
            }
            else
            {
                var other = (i - ChunksPerFile) % otherFileCount;
                filePath = $"C:\\bench\\other-{other:D6}.bin";
                chunkIndex = (i - ChunksPerFile) / otherFileCount;
            }

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
                        FilePath = filePath,
                        ChunkIndex = chunkIndex,
                        ReferencedAt = now
                    }
                ]
            });
        }
        _databaseService.BulkInsertChunkIndexEntries(entries);

        // Build the reverse index for the Phase 5 path.
        _databaseService.RebuildReverseChunkIndex();

        _targetFile = "C:\\bench\\target.bin";
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Phase 5 fast path: indexed reverse lookup. Cost scales with
    /// <see cref="ChunksPerFile"/>, not <see cref="TotalChunks"/>.
    /// </summary>
    [Benchmark(Description = "Phase5: reverse-index lookup")]
    public int Phase5_ReverseIndex()
    {
        return _databaseService.GetChunkEntriesForFile(_targetFile).Count;
    }

    /// <summary>
    /// Legacy baseline: full scan over chunk_index + in-memory filter.
    /// Cost scales with <see cref="TotalChunks"/>.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Legacy: full chunk-index scan")]
    public int Legacy_FullScan()
    {
        return _databaseService.GetChunkEntriesForFile_LegacyScan(_targetFile).Count;
    }
}

/// <summary>
/// Shared helpers for Phase 5 benchmarks.
/// </summary>
internal static class BenchDataHelper
{
    /// <summary>
    /// Deterministic 64-char hex derived from the seed; not cryptographic.
    /// </summary>
    public static string HashString(int seed)
    {
        Span<byte> bytes = stackalloc byte[32];
        BitConverter.TryWriteBytes(bytes, seed);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
