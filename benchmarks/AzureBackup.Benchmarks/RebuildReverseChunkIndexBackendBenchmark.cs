using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Option C / C-3 (3/N): head-to-head comparison of
/// <c>RebuildReverseChunkIndex</c> on the LiteDB and SQLite backends.
///
/// <para>
/// This is the second of five C-3 decision-gate scenarios. The
/// existing <c>RebuildReverseChunkIndexBenchmark</c> measures the
/// LiteDB path in isolation; this benchmark runs both backends
/// through the same logical workload so the ratio column drives the
/// eval-doc decision (\u00a79).
/// </para>
///
/// <para>
/// <b>Design choice for SQLite leg.</b> In production the SQLite
/// backend writes <c>chunk_file_refs</c> as a side-effect of
/// <c>SaveBackedUpFile</c>, so <c>RebuildReverseChunkIndex</c> only
/// has work to do for an upgrading user (file rows already exist from
/// the LiteDB import but the reverse index hasn't been built yet).
/// The benchmark stages exactly that scenario: bulk-load files +
/// chunk_index, then wipe chunk_file_refs to simulate the
/// post-migration state, then time the rebuild.
/// </para>
///
/// <para>
/// <b>InvocationCount = 1.</b> Each iteration must start with an
/// unbuilt reverse index, which means a fresh DB (or a wiped one).
/// We pay the seed cost once per invocation; BenchmarkDotNet would
/// otherwise run hundreds of invocations and the seed time would
/// dominate the wall clock.
/// </para>
///
/// <para>
/// <b>C-3 (3c-3) - BI1.</b> Switched from RunStrategy.Monitoring to
/// RunStrategy.Throughput with iterationCount = 5. Monitoring is meant
/// for VERY long-running workloads and uses few iterations; the wide
/// confidence intervals in the C-3 (3b) result (e.g. 853 ms error on a
/// 412 ms LiteDB measurement) reflected that. Throughput with 5
/// iterations gives BDN enough samples to compute tighter error bars
/// without piling on excessive wall-clock cost.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 5, invocationCount: 1)]
public class RebuildReverseChunkIndexBackendBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int ChunksPerFile = 100;

    /// <summary>
    /// Database-wide chunk count. Same axis as the existing
    /// <c>RebuildReverseChunkIndexBenchmark</c> so the LiteDB numbers
    /// here cross-check against the post-Phase-5 baseline.
    /// </summary>
    [Params(10_000, 100_000, 500_000)]
    public int TotalChunks { get; set; }

    /// <summary>
    /// Backend selector - drives LiteDbBackend vs SqliteBackend. Each
    /// invocation re-initialises the chosen backend in a fresh temp
    /// directory.
    /// </summary>
    [Params("LiteDB", "SQLite")]
    public string Backend { get; set; } = "LiteDB";

    private string _testDir = string.Empty;
    private IDatabaseBackend _backend = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [chunks={TotalChunks},backend={Backend}] {what}");

        Mark("creating temp dir");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-rebuild-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        Mark("opening backend");
        _backend = Backend switch
        {
            "LiteDB" => new LiteDbBackend(),
            "SQLite" => new SqliteBackend(),
            _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
        };
        _backend.Initialize(Path.Combine(_testDir, "bench.db"), Password.AsSpan());

        Mark("building in-memory data");
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        var fileCount = Math.Max(1, TotalChunks / ChunksPerFile);

        // For SQLite we ALSO need files + file_chunks rows because the
        // SQLite RebuildReverseChunkIndex backfills from file_chunks,
        // not from a per-entry ReferencingFiles list. Build both shapes
        // in one pass.
        var fileToChunks = new Dictionary<string, List<ChunkInfo>>();

        for (int i = 0; i < TotalChunks; i++)
        {
            var fileIndex = i / ChunksPerFile % fileCount;
            var filePath = $@"C:\bench\file-{fileIndex:D6}.bin";
            var chunkIndex = i % ChunksPerFile;
            var hash = BenchDataHelper.HashString(i);

            entries.Add(new ChunkIndexEntry
            {
                ChunkHash = hash,
                FirstUploadedAt = now,
                SizeBytes = 65_536,
                ReferenceCount = 1,
                LastVerifiedAt = now,
                ReferencingFiles =
                [
                    new ChunkFileReference
                    {
                        FilePath = filePath,
                        ChunkIndex = chunkIndex,
                        ReferencedAt = now,
                    }
                ],
            });

            if (!fileToChunks.TryGetValue(filePath, out var list))
            {
                list = new List<ChunkInfo>();
                fileToChunks[filePath] = list;
            }
            list.Add(new ChunkInfo
            {
                Index = chunkIndex,
                Offset = chunkIndex * 65_536L,
                Length = 65_536,
                Hash = hash,
                BlobName = "chunks/" + hash,
            });
        }

        Mark($"inserting chunk_index ({entries.Count} entries)");
        _backend.BulkInsertChunkIndexEntries(entries);

        if (_backend is SqliteBackend sqlite)
        {
            // SQLite's RebuildReverseChunkIndex backfills from file_chunks,
            // so we need files + file_chunks populated. The bulk helper
            // also writes chunk_file_refs (steady-state SaveBackedUpFile
            // behaviour) - we wipe those after so the rebuild has work.
            var allFiles = new List<BackedUpFile>(fileToChunks.Count);
            foreach (var (path, chunks) in fileToChunks)
            {
                allFiles.Add(new BackedUpFile
                {
                    LocalPath = path,
                    BlobName = "metadata/" + Path.GetFileName(path) + ".json",
                    FileSize = chunks.Sum(c => (long)c.Length),
                    LastModified = now,
                    FileHash = "FILE-" + path.GetHashCode().ToString("X8"),
                    Status = BackupStatus.Completed,
                    BackedUpAt = now,
                    MetadataVersion = 1,
                    Chunks = chunks,
                });
            }
            Mark($"SQLite: bulk-inserting {allFiles.Count} files");
            sqlite.BulkInsertFiles(allFiles);
            Mark("SQLite: clearing reverse index to simulate post-migration state");
            sqlite.ClearReverseChunkIndexForBenchmark();
        }
        // LiteDB path: leave the reverse index unbuilt - that is the
        // exact state RebuildReverseChunkIndex is designed to fix.

        Mark($"setup complete (total {sw.ElapsedMilliseconds} ms)");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _backend?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// One-time reverse-index rebuild. The whole point of the benchmark
    /// is comparing the wall-clock + allocation cost of this single
    /// operation across backends.
    /// </summary>
    [Benchmark]
    public void Rebuild()
    {
        _backend.RebuildReverseChunkIndex();
    }
}
