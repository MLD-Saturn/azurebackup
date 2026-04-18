using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Option C / C-3 (5/N): head-to-head comparison of
/// <c>SaveBackedUpFile</c> on the LiteDB and SQLite backends. Two
/// benchmark methods cover the two production regimes:
///
/// <list type="number">
///   <item><c>FirstSave</c>: file does not exist yet. LiteDB inserts
///     one BSON document; SQLite executes one INSERT into <c>files</c>,
///     N INSERTs into <c>file_chunks</c>, and N INSERTs into
///     <c>chunk_file_refs</c>, all in one transaction.</item>
///   <item><c>ReSave</c>: file already exists with the same logical
///     identity but new content. LiteDB upserts one document. SQLite
///     does the full UPSERT-DELETE-INSERT dance: UPSERT on <c>files</c>,
///     DELETE+N INSERTs on <c>file_chunks</c>, DELETE+N INSERTs on
///     <c>chunk_file_refs</c>. This is the regime where SQLite's
///     statement overhead is most exposed.</item>
/// </list>
///
/// <para>
/// <b>Why this scenario matters.</b> The sync agent calls
/// SaveBackedUpFile on every file it processes. A backup loop that
/// sustains 100 files/sec must complete each save in under 10 ms. Both
/// backends should be far below that, but the relative cost determines
/// whether SQLite can keep up with high-volume backup loops without
/// becoming the bottleneck.
/// </para>
///
/// <para>
/// <b>Per-iteration semantics.</b> [IterationSetup] resets the DB to
/// an empty state for FirstSave, or to a populated-with-old-content
/// state for ReSave. So each [Benchmark] timing measures exactly one
/// SaveBackedUpFile call, not a compounding sequence.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 5, invocationCount: 1)]
public class SaveBackedUpFileBackendBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const string TargetFilePath = @"C:\bench\target.bin";

    [Params("LiteDB", "SQLite")]
    public string Backend { get; set; } = "LiteDB";

    /// <summary>
    /// Number of chunks the saved file references. Spans the realistic
    /// range from "small file" (1 chunk) to "typical media" (10) to
    /// "large archive" (100). Production sees a long tail beyond 100
    /// for VM-image-class files; not benchmarked here to keep the
    /// matrix small.
    /// </summary>
    [Params(1, 10, 100)]
    public int ChunkCount { get; set; }

    private string _testDir = string.Empty;
    private IDatabaseBackend _backend = null!;
    private BackedUpFile _toSave = null!;

    /// <summary>
    /// Runs once per (Backend, ChunkCount) parameter combination. Sets
    /// up the temp dir, opens the backend, and pre-builds the
    /// BackedUpFile object that every iteration will save. Iterations
    /// will MUTATE the DB state but not the in-memory object.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [chunks={ChunkCount},backend={Backend}] {what}");

        Mark("creating temp dir");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-savefile-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        Mark("opening backend");
        _backend = Backend switch
        {
            "LiteDB" => new LiteDbBackend(),
            "SQLite" => new SqliteBackend(),
            _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
        };
        _backend.Initialize(Path.Combine(_testDir, "bench.db"), Password.AsSpan());

        Mark("building BackedUpFile to save");
        _toSave = MakeFile(TargetFilePath, ChunkCount, isResave: false);

        // Integrity check: one save round-trips correctly. Validates the
        // pipeline before any [Benchmark] runs.
        Mark("integrity check");
        _backend.SaveBackedUpFile(_toSave);
        var loaded = _backend.GetBackedUpFile(TargetFilePath);
        if (loaded == null || loaded.Chunks.Count != ChunkCount)
        {
            throw new InvalidOperationException(
                $"Integrity check failed: backend={Backend}, expected {ChunkCount} chunks, got {loaded?.Chunks.Count ?? -1}");
        }

        // Reset the DB to its empty state. We rely on Dispose+reopen so
        // the chunk_file_refs and file_chunks tables are also cleared.
        // (DELETE FROM files would only cascade-delete file_chunks via
        // FK; chunk_file_refs has no FK and would persist.)
        Mark("resetting DB to empty state for first benchmark iteration");
        _backend.Dispose();
        File.Delete(Path.Combine(_testDir, "bench.db"));
        // The salt file may also exist; recreate fresh so the next
        // Initialize derives a fresh key pair (still works because
        // password is identical).
        var saltPath = Path.Combine(_testDir, "bench.db.salt");
        if (File.Exists(saltPath)) File.Delete(saltPath);
        _backend = Backend switch
        {
            "LiteDB" => new LiteDbBackend(),
            "SQLite" => new SqliteBackend(),
            _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
        };
        _backend.Initialize(Path.Combine(_testDir, "bench.db"), Password.AsSpan());

        Mark($"setup complete (total {sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Runs before EACH iteration of EACH benchmark method. For
    /// FirstSave it deletes the existing row (if any). For ReSave it
    /// ensures the row exists with old content so the [Benchmark] call
    /// hits the upsert path.
    /// </summary>
    [IterationSetup(Target = nameof(FirstSave))]
    public void IterationSetup_FirstSave()
    {
        // Clear any previous file row + its cascaded file_chunks rows.
        // chunk_file_refs has no FK so we need an explicit cleanup; the
        // simplest cross-backend way is to dispose+recreate the file
        // row via a direct primary-key-conflict UPSERT to an "empty"
        // shape, but neither backend exposes that. Easiest: re-save
        // an empty-chunks row with the same path before each iteration.
        // Both backends treat that as an UPSERT with zero-length chunk
        // list, which clears the dependent rows as a side effect.
        var empty = new BackedUpFile
        {
            LocalPath = TargetFilePath,
            BlobName = "metadata/target.bin.json",
            FileSize = 0,
            LastModified = DateTime.UtcNow,
            FileHash = "EMPTY",
            Status = BackupStatus.Pending,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
        };
        _backend.SaveBackedUpFile(empty);
        // Now delete to leave a clean "no row" state for FirstSave.
        // Neither IDatabaseBackend exposes a public delete, so we
        // skip the delete - the next SaveBackedUpFile will hit the
        // UPSERT path on both backends. This means FirstSave is in
        // practice "upsert-when-row-exists-with-empty-chunks". Honest
        // disclosure documented on the [Benchmark] method below.
    }

    [IterationSetup(Target = nameof(ReSave))]
    public void IterationSetup_ReSave()
    {
        // Pre-populate with the SAME chunk count so the [Benchmark]
        // call exercises the realistic re-save path: file row exists,
        // chunk count matches, but the DELETE+INSERT-N still fires.
        _backend.SaveBackedUpFile(_toSave);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _backend?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// First-save path. Note: because IDatabaseBackend has no public
    /// "delete" primitive and IterationSetup cannot leave the DB in a
    /// truly row-less state, this benchmark in practice measures
    /// "upsert when an empty-chunks row already exists" - which is
    /// still the INSERT-into-file_chunks path because the prior row
    /// had no chunks to delete. The files-table side is an UPDATE,
    /// not an INSERT, so the absolute cost is slightly higher than a
    /// pure first-save. The LiteDB and SQLite legs see identical setup
    /// so the ratio remains valid.
    /// </summary>
    [Benchmark]
    public void FirstSave()
    {
        _backend.SaveBackedUpFile(_toSave);
    }

    /// <summary>
    /// Re-save path. The file row already exists with the full chunk
    /// list from IterationSetup_ReSave; the [Benchmark] call exercises
    /// the full UPSERT + DELETE-existing-chunks + INSERT-new-chunks
    /// + DELETE-existing-refs + INSERT-new-refs path on SQLite. LiteDB
    /// upserts one BSON document.
    /// </summary>
    [Benchmark]
    public void ReSave()
    {
        _backend.SaveBackedUpFile(_toSave);
    }

    private static BackedUpFile MakeFile(string path, int chunkCount, bool isResave)
    {
        var when = new DateTime(2026, 4, 18, 1, 0, 0, DateTimeKind.Utc);
        var file = new BackedUpFile
        {
            LocalPath = path,
            BlobName = $"metadata/{Path.GetFileName(path)}.json",
            FileSize = chunkCount * 65_536L,
            LastModified = when,
            FileHash = (isResave ? "RESAVE-" : "FIRST-") + path.GetHashCode().ToString("X8"),
            Status = BackupStatus.Completed,
            BackedUpAt = when,
            MetadataVersion = 1,
        };
        for (var i = 0; i < chunkCount; i++)
        {
            file.Chunks.Add(new ChunkInfo
            {
                Index = i,
                Offset = i * 65_536L,
                Length = 65_536,
                Hash = BenchDataHelper.HashString(i),
                BlobName = "chunks/" + BenchDataHelper.HashString(i),
            });
        }
        return file;
    }
}
