using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Option C / C-3 (4/N): head-to-head comparison of concurrent
/// <c>GetChunkEntriesForFile</c> readers across three legs:
///
/// <list type="number">
///   <item><c>LiteDB</c>: production LiteDbBackend - readers serialise
///     through the wrapping LocalDatabaseService's RWLock (many readers
///     allowed concurrently, but every reader hits the same single
///     LiteDB connection).</item>
///   <item><c>SQLiteSingleConn</c>: production SqliteBackend as it
///     stands today - one connection, Microsoft.Data.Sqlite serialises
///     concurrent commands via internal locking. This is the as-shipped
///     concurrency story for the SQLite backend.</item>
///   <item><c>SQLitePooled</c>: a read-only connection pool spun up
///     IN THE BENCHMARK (NOT in SqliteBackend) that opens N parallel
///     read connections, each PRAGMA-keyed and WAL-readonly. This is
///     the target architecture we would build in "C-1 final step b" if
///     we decide to ship Option C. Including it here lets the decision
///     gate compare against SQLite's structural ceiling, not just
///     against today's single-connection implementation.</item>
/// </list>
///
/// <para>
/// <b>What this benchmark does NOT cover.</b> Reader/writer contention
/// (a writer holding the lock while N readers wait) is not measured
/// here. That is the actual production hotspot Phase 5 / P2 was
/// motivated by. A future C-3 (4b) could add a writer that runs in
/// parallel with the read fan-out; for now we measure pure-read
/// concurrency to compare against the existing
/// <c>ConcurrentGetChunkEntriesBenchmark</c>.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ConcurrentReadersBackendBenchmark
{
    private const string Password = "BenchmarkPassword123!";
    private const int TotalChunks = 100_000;
    private const int ChunksPerFile = 100;
    private const int FilesCount = 1_000;

    /// <summary>
    /// Backend selector. Three legs - see class XML doc for what each
    /// represents.
    /// </summary>
    [Params("LiteDB", "SQLiteSingleConn", "SQLitePooled")]
    public string Backend { get; set; } = "LiteDB";

    /// <summary>
    /// Number of threads calling <c>GetChunkEntriesForFile</c> at once.
    /// Same axis as the existing single-backend
    /// <c>ConcurrentGetChunkEntriesBenchmark</c>.
    /// </summary>
    [Params(1, 4, 16)]
    public int ConcurrentReaders { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LiteDbBackend? _liteDb;
    private SqliteBackend? _sqlite;
    private SqlitePooledReader? _sqlitePool;
    private string[] _targetFiles = Array.Empty<string>();

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [readers={ConcurrentReaders},backend={Backend}] {what}");

        Mark("creating temp dir");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-concread-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        // Stage data once per backend in GlobalSetup; the workload is
        // pure read so iterations don't mutate the DB.
        Mark("building in-memory data");
        var now = DateTime.UtcNow;
        var entries = new List<ChunkIndexEntry>(TotalChunks);
        var fileToChunks = new Dictionary<string, List<ChunkInfo>>();

        for (int i = 0; i < TotalChunks; i++)
        {
            var fileIndex = i / ChunksPerFile % FilesCount;
            var chunkIndex = i % ChunksPerFile;
            var hash = BenchDataHelper.HashString(i);
            var filePath = $@"C:\bench\file-{fileIndex:D6}.bin";

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

        Mark("opening backend + populating");
        switch (Backend)
        {
            case "LiteDB":
                _liteDb = new LiteDbBackend();
                _liteDb.Initialize(_dbPath, Password.AsSpan());
                _liteDb.BulkInsertChunkIndexEntries(entries);
                _liteDb.RebuildReverseChunkIndex();
                break;

            case "SQLiteSingleConn":
            case "SQLitePooled":
                _sqlite = new SqliteBackend();
                _sqlite.Initialize(_dbPath, Password.AsSpan());
                _sqlite.BulkInsertChunkIndexEntries(entries);
                var allFiles = fileToChunks.Select(kv => new BackedUpFile
                {
                    LocalPath = kv.Key,
                    BlobName = "metadata/" + Path.GetFileName(kv.Key) + ".json",
                    FileSize = kv.Value.Sum(c => (long)c.Length),
                    LastModified = now,
                    FileHash = "FILE-" + kv.Key.GetHashCode().ToString("X8"),
                    Status = BackupStatus.Completed,
                    BackedUpAt = now,
                    MetadataVersion = 1,
                    Chunks = kv.Value,
                }).ToList();
                _sqlite.BulkInsertFiles(allFiles);

                if (Backend == "SQLitePooled")
                {
                    Mark($"opening {ConcurrentReaders} pooled read connections");
                    _sqlitePool = new SqlitePooledReader(_dbPath, Password,
                        ConcurrentReaders);
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown backend: {Backend}");
        }

        // Each reader queries a different file so the DB pages touched
        // spread across the index (avoids cache-hit bias).
        _targetFiles = new string[ConcurrentReaders];
        for (int i = 0; i < ConcurrentReaders; i++)
        {
            _targetFiles[i] = $@"C:\bench\file-{i * 17 % FilesCount:D6}.bin";
        }

        // Integrity check before any [Benchmark] runs: the chosen leg
        // must be able to read back the seeded data, and the pool (when
        // active) must return the SAME count on every connection. If
        // any leg fails this we want to crash now, not produce
        // misleading benchmark numbers.
        Mark("integrity check");
        var expected = ChunksPerFile;
        var sampleFile = _targetFiles[0];
        int sample = Backend switch
        {
            "LiteDB" => _liteDb!.GetChunkEntriesForFile(sampleFile).Count,
            "SQLiteSingleConn" => _sqlite!.GetChunkEntriesForFile(sampleFile).Count,
            "SQLitePooled" => _sqlitePool!.GetChunkEntriesForFile(sampleFile, 0).Count,
            _ => -1,
        };
        if (sample != expected)
        {
            throw new InvalidOperationException(
                $"Integrity check failed: backend={Backend}, file={sampleFile}, expected={expected}, got={sample}");
        }
        if (Backend == "SQLitePooled")
        {
            // Make sure every pooled connection actually opened correctly
            // by routing one read through each.
            for (int i = 0; i < ConcurrentReaders; i++)
            {
                var c = _sqlitePool!.GetChunkEntriesForFile(_targetFiles[i], i).Count;
                if (c != expected)
                {
                    throw new InvalidOperationException(
                        $"Pool connection {i} returned {c}, expected {expected}");
                }
            }
        }

        Mark($"setup complete (total {sw.ElapsedMilliseconds} ms)");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _liteDb?.Dispose();
        _sqlite?.Dispose();
        _sqlitePool?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private readonly object _singleConnLock = new();

    /// <summary>
    /// Fan-out N parallel <c>GetChunkEntriesForFile</c> calls and wait
    /// for all to complete. Returns the result count from the first
    /// task as a smoke check that the queries actually executed.
    ///
    /// <para>
    /// <b>SingleConn locking note.</b> Microsoft.Data.Sqlite does not
    /// permit two threads to use the same SqliteConnection concurrently;
    /// the first attempt at this benchmark crashed at 4 readers with
    /// <c>List.RemoveAt: index out of range</c> from inside M.D.Sqlite's
    /// command-tracker. The lock here makes the as-shipped SqliteBackend
    /// behave honestly (every reader serialises through one connection),
    /// which is what production code calling SqliteBackend today already
    /// has to do via the wrapping LocalDatabaseService RWLock. The
    /// resulting numbers reflect that constraint accurately.
    /// </para>
    /// </summary>
    [Benchmark]
    public int Concurrent()
    {
        var tasks = new Task<int>[ConcurrentReaders];
        for (int i = 0; i < ConcurrentReaders; i++)
        {
            var file = _targetFiles[i];
            var idx = i;
            tasks[i] = Backend switch
            {
                "LiteDB" => Task.Run(() => _liteDb!.GetChunkEntriesForFile(file).Count),
                "SQLiteSingleConn" => Task.Run(() =>
                {
                    lock (_singleConnLock)
                    {
                        return _sqlite!.GetChunkEntriesForFile(file).Count;
                    }
                }),
                "SQLitePooled" => Task.Run(() => _sqlitePool!.GetChunkEntriesForFile(file, idx).Count),
                _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
            };
        }
        Task.WaitAll(tasks);
        return tasks[0].Result;
    }
}

/// <summary>
/// Benchmark-only: a tiny pool of pre-keyed read-only SqliteConnections
/// that mirrors what a future SqliteBackend connection pool would look
/// like. NOT a production class - lives in the benchmark project on
/// purpose so we can measure SQLite's structural ceiling for the C-3
/// decision gate without committing to the real pool design today.
///
/// <para>
/// Each connection is opened with the same SQLCipher key as the writer,
/// then immediately set to <c>read_uncommitted = false</c> +
/// <c>query_only = true</c> so accidental writes through these handles
/// fail loudly. WAL mode (set up by SqliteBackend during Initialize) is
/// what allows multiple read connections to make progress concurrently.
/// </para>
/// </summary>
internal sealed class SqlitePooledReader : IDisposable
{
    private readonly SqliteConnection[] _connections;
    private bool _disposed;

    public SqlitePooledReader(string databasePath, string password, int connectionCount)
    {
        _connections = new SqliteConnection[connectionCount];
        for (int i = 0; i < connectionCount; i++)
        {
            // Reuse SqliteBackend's exact key-derivation + PRAGMA-tuning
            // pipeline. The earlier hand-rolled PBKDF2 pool diverged from
            // the writer in three ways (different salt, different KDF,
            // wrong cipher_compatibility reset) and every cell failed with
            // SQLITE_NOTADB. Routing through the factory eliminates all
            // three classes of drift by construction - if the writer
            // changes its keying, the pool follows automatically.
            _connections[i] = SqliteBackend.OpenReadOnlyForBenchmark(
                databasePath, password.AsSpan());
        }
    }

    /// <summary>
    /// Looks up chunk entries for <paramref name="filePath"/> using the
    /// connection at <paramref name="connectionIndex"/>. The caller is
    /// responsible for ensuring no two threads share the same index
    /// (the benchmark dispatches connectionIndex == thread index).
    /// </summary>
    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath, int connectionIndex)
    {
        var conn = _connections[connectionIndex];
        var result = new List<ChunkIndexEntry>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT
                ci.chunk_hash, ci.first_uploaded_at, ci.original_uploader_path,
                ci.size_bytes, ci.reference_count, ci.current_tier, ci.last_verified_at
            FROM chunk_file_refs cfr
            INNER JOIN chunk_index ci ON ci.chunk_hash = cfr.chunk_hash
            WHERE cfr.file_path = $file_path;
            """;
        cmd.Parameters.AddWithValue("$file_path", filePath);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChunkIndexEntry
            {
                ChunkHash = reader.GetString(0),
                FirstUploadedAt = ParseUtc(reader.GetString(1)),
                OriginalUploaderPath = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                ReferenceCount = reader.GetInt32(4),
                CurrentTier = (StorageTier)reader.GetInt32(5),
                LastVerifiedAt = ParseUtc(reader.GetString(6)),
            });
        }
        return result;
    }

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var conn in _connections)
        {
            try { conn.Close(); } catch { /* best effort */ }
            conn.Dispose();
        }
    }
}
