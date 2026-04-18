using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using AzureBackup.Core.Services.Backends;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Option C / C-3 (1/N): head-to-head comparison of
/// <c>GetChunkEntriesForFile</c> on the LiteDB and SQLite backends.
///
/// <para>
/// This is the headline scenario from the eval doc \u00a76 - the one
/// projected to show ~10x speedup on SQLite because the LiteDB code
/// path was forced into the per-chunk-FindOne shape by the
/// "Method Contains not available to convert to BsonExpression" pitfall
/// (the b0c9439 regression). SQLite uses a clean SELECT JOIN with
/// indexes on both sides.
/// </para>
///
/// <para>
/// <b>Benchmark contract.</b> Both legs build identical data: the
/// target file references <c>ChunksPerFile</c> chunks in a database of
/// <c>TotalChunks</c> total chunks. The result count is asserted to be
/// equal between the two legs in <see cref="GlobalSetup"/> as a smoke
/// check that the comparison is apples-to-apples.
/// </para>
///
/// <para>
/// <b>How to read the output.</b> The "Ratio" column compares each
/// row to the LiteDB baseline. Less than 1.0 means SQLite is faster.
/// The eval doc decision gate (\u00a79) requires <c>Ratio &lt; 0.5</c> on at
/// least 3 of 5 scenarios for Option C to ship.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class GetChunkEntriesForFileBackendBenchmark
{
    private const string Password = "BenchmarkPassword123!";

    /// <summary>
    /// Database-wide chunk count. 500 000 mirrors the eval doc's reference
    /// scale (~500 GB backup at 1 MB target chunk size).
    /// </summary>
    [Params(10_000, 100_000, 500_000)]
    public int TotalChunks { get; set; }

    /// <summary>
    /// Number of chunks the target file references. Independent of
    /// <see cref="TotalChunks"/> - that's the whole point of the
    /// reverse index.
    /// </summary>
    [Params(100, 1_000)]
    public int ChunksPerFile { get; set; }

    private const string TargetFilePath = @"C:\bench\target.bin";

    private string _liteDir = string.Empty;
    private string _sqliteDir = string.Empty;
    private LiteDbBackend _liteDb = null!;
    private SqliteBackend _sqlite = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // BenchmarkDotNet swallows GlobalSetup output by default but writes
        // it to BenchmarkDotNet.Artifacts/<bench>-job-*.log. The progress
        // markers below let a curious observer (or a stuck CI run) tell
        // whether setup is making forward progress vs actually wedged.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [chunks={TotalChunks},perFile={ChunksPerFile}] {what}");

        Mark("creating temp dirs");
        _liteDir = Path.Combine(Path.GetTempPath(), "azbk-bench-lite-" + Guid.NewGuid().ToString("N"));
        _sqliteDir = Path.Combine(Path.GetTempPath(), "azbk-bench-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_liteDir);
        Directory.CreateDirectory(_sqliteDir);

        Mark("opening LiteDB backend");
        _liteDb = new LiteDbBackend();
        _liteDb.Initialize(Path.Combine(_liteDir, "bench.db"), Password.AsSpan());

        Mark("opening SQLite backend");
        _sqlite = new SqliteBackend();
        _sqlite.Initialize(Path.Combine(_sqliteDir, "bench.db"), Password.AsSpan());

        Mark("building in-memory data");
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        var otherFileCount = Math.Max(1, (TotalChunks - ChunksPerFile) / 10);

        var targetFileChunks = new List<ChunkInfo>(ChunksPerFile);
        var otherFileChunks = new Dictionary<string, List<ChunkInfo>>();

        for (int i = 0; i < TotalChunks; i++)
        {
            string filePath;
            int chunkIndex;
            if (i < ChunksPerFile)
            {
                filePath = TargetFilePath;
                chunkIndex = i;
            }
            else
            {
                var other = (i - ChunksPerFile) % otherFileCount;
                filePath = $@"C:\bench\other-{other:D6}.bin";
                chunkIndex = (i - ChunksPerFile) / otherFileCount;
            }

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

            var info = new ChunkInfo
            {
                Index = chunkIndex,
                Offset = chunkIndex * 65_536L,
                Length = 65_536,
                Hash = hash,
                BlobName = "chunks/" + hash,
            };
            if (filePath == TargetFilePath)
            {
                targetFileChunks.Add(info);
            }
            else
            {
                if (!otherFileChunks.TryGetValue(filePath, out var list))
                {
                    list = new List<ChunkInfo>();
                    otherFileChunks[filePath] = list;
                }
                list.Add(info);
            }
        }
        Mark($"in-memory data built: {entries.Count} entries, {otherFileChunks.Count + 1} files");

        // ---- Populate LiteDB ----
        Mark("LiteDB: BulkInsertChunkIndexEntries");
        _liteDb.BulkInsertChunkIndexEntries(entries);
        Mark("LiteDB: RebuildReverseChunkIndex");
        _liteDb.RebuildReverseChunkIndex();
        Mark("LiteDB: populated");

        // ---- Populate SQLite ----
        // BulkInsertFilesForBenchmark is a benchmark-only helper that writes
        // files + file_chunks + chunk_file_refs in ONE transaction, sidestepping
        // the per-file transaction overhead of SaveBackedUpFile. At C-3 scale
        // (50K other files) that's the difference between ~5 seconds and
        // ~5 minutes of setup time per parameter combination.
        Mark("SQLite: BulkInsertChunkIndexEntries");
        _sqlite.BulkInsertChunkIndexEntries(entries);

        var allFiles = new List<BackedUpFile>(otherFileChunks.Count + 1)
        {
            NewFile(TargetFilePath, targetFileChunks, now)
        };
        foreach (var (path, chunks) in otherFileChunks)
        {
            allFiles.Add(NewFile(path, chunks, now));
        }
        Mark($"SQLite: BulkInsertFilesForBenchmark ({allFiles.Count} files)");
        _sqlite.BulkInsertFilesForBenchmark(allFiles);
        Mark("SQLite: populated");

        // Sanity smoke: both backends return the same count for the target.
        Mark("integrity check");
        var liteCount = _liteDb.GetChunkEntriesForFile(TargetFilePath).Count;
        var sqliteCount = _sqlite.GetChunkEntriesForFile(TargetFilePath).Count;
        if (liteCount != ChunksPerFile || sqliteCount != ChunksPerFile)
        {
            throw new InvalidOperationException(
                $"Setup integrity check failed: LiteDB={liteCount}, SQLite={sqliteCount}, expected={ChunksPerFile}");
        }
        Mark($"setup complete (total {sw.ElapsedMilliseconds} ms)");
    }

    private static BackedUpFile NewFile(string path, List<ChunkInfo> chunks, DateTime now)
        => new()
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
        };

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _liteDb?.Dispose();
        _sqlite?.Dispose();
        try { Directory.Delete(_liteDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_sqliteDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// LiteDB baseline: indexed reverse lookup against chunk_file_refs
    /// followed by per-hash FindOne. The "FindOne in a loop" shape is the
    /// design forced on us by LiteDB's expression-tree limits (Phase 5 / P3).
    /// </summary>
    [Benchmark(Baseline = true, Description = "LiteDB: reverse-index + FindOne loop")]
    public int LiteDb_ReverseIndex()
    {
        return _liteDb.GetChunkEntriesForFile(TargetFilePath).Count;
    }

    /// <summary>
    /// SQLite candidate: single SELECT DISTINCT JOIN of chunk_file_refs
    /// and chunk_index. Cost scales with ChunksPerFile only; the JOIN is
    /// indexed on both sides.
    /// </summary>
    [Benchmark(Description = "SQLite: indexed JOIN")]
    public int Sqlite_Join()
    {
        return _sqlite.GetChunkEntriesForFile(TargetFilePath).Count;
    }
}
