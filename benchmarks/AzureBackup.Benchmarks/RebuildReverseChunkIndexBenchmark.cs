using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P3: measures the one-time
/// <see cref="LocalDatabaseService.RebuildReverseChunkIndex"/> migration.
/// Validates the synchronous-modal-progress UX decision by showing how
/// long the rebuild blocks login on a realistic database.
///
/// <para>
/// The rebuild runs once per upgraded database. At the benchmarked sizes
/// it is short enough that blocking the login flow is acceptable; if the
/// numbers grew beyond ~60 seconds we would need to reconsider an async
/// / background-with-status UX.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class RebuildReverseChunkIndexBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int ChunksPerFile = 100;

    [Params(10_000, 100_000, 500_000)]
    public int TotalChunks { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LocalDatabaseService _databaseService = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh DB per iteration so the rebuild always starts from an
        // unbuilt reverse index.
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, Password);

        // Seed primary chunk_index with ReferencingFiles populated so the
        // rebuild has work to do. Spread across enough files to mimic a
        // realistic fan-out (ChunksPerFile chunks per file).
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        var fileCount = Math.Max(1, TotalChunks / ChunksPerFile);
        for (int i = 0; i < TotalChunks; i++)
        {
            var fileIndex = i / ChunksPerFile % fileCount;
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
        // Leave the reverse index unbuilt - that is exactly what the
        // benchmark is measuring.
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "Phase5: one-time reverse-index rebuild")]
    public void Rebuild()
    {
        _databaseService.RebuildReverseChunkIndex();
    }
}
