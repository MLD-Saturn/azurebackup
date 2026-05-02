using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1a: end-to-end smoke test for the SQLite + SQLCipher
/// integration. Proves the encryption stack works on .NET 10 before any
/// real persistence code is written against it.
///
/// <para>
/// What is tested:
/// </para>
/// <list type="bullet">
///   <item>Open an encrypted DB at a fresh path - schema is created, the
///     connection is usable, a salt file appears.</item>
///   <item>Close, reopen with the same password - data is readable, the
///     schema persists.</item>
///   <item>Reopen with the wrong password - throws
///     <see cref="InvalidPasswordException"/> rather than silently returning
///     an empty / corrupt DB.</item>
/// </list>
///
/// <para>
/// These are intentionally small assertions; the full functional surface is
/// proved later by the existing 536 tests once the backend is wired into
/// <c>LocalDatabaseService</c>.
/// </para>
/// </summary>
public class SqliteBackendSmokeTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public SqliteBackendSmokeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "smoke.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initialize_NewDatabase_CreatesSchemaAndSaltFile()
    {
        // Arrange
        using var backend = new SqliteBackend();
        const string password = "SmokeTestPassword123!";

        // Act
        backend.Initialize(_dbPath, password.AsSpan());

        // Assert
        Assert.True(backend.IsInitialized);
        Assert.Equal(_dbPath, backend.DatabasePath);
        Assert.True(File.Exists(_dbPath), "Database file should exist on disk");
        Assert.True(File.Exists(_dbPath + ".salt"), "Salt file should be created next to the database");

        // SQLite version is reachable -> connection is alive and decrypted.
        var version = backend.ReadSqliteVersion();
        Assert.False(string.IsNullOrEmpty(version), "Should be able to read sqlite_version()");

        // Schema was created. We expect every table from CreateSchema().
        // 10 base tables + 2 added by D1 (integrity_check_runs,
        // integrity_check_failures) = 12.
        var tableCount = backend.CountSchemaTables();
        Assert.Equal(12, tableCount);
    }

    [Fact]
    public void Initialize_ReopenWithSamePassword_Succeeds()
    {
        // Arrange
        const string password = "SmokeTestPassword123!";

        using (var first = new SqliteBackend())
        {
            first.Initialize(_dbPath, password.AsSpan());
            Assert.Equal(12, first.CountSchemaTables());
            // first.Dispose() runs here, closing the connection.
        }

        // Act: reopen with the same password.
        using var second = new SqliteBackend();
        second.Initialize(_dbPath, password.AsSpan());

        // Assert: schema persisted, no exception, connection works.
        Assert.True(second.IsInitialized);
        Assert.Equal(12, second.CountSchemaTables());
    }

    [Fact]
    public void Initialize_NewDatabase_LoadedNativeIsSqlcipher()
    {
        // Critical guard: if the wrong native bundle ships, encryption is
        // silently a no-op and the wrong-password test below would pass
        // for the wrong reason. PRAGMA cipher_version returns null on a
        // plain SQLite build and the SQLCipher version string on a
        // SQLCipher build.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "ProveSqlcipherIsLoaded".AsSpan());

        var version = backend.ReadSqlcipherVersion();
        Assert.False(string.IsNullOrEmpty(version),
            "PRAGMA cipher_version returned empty - the loaded native library is plain SQLite, not SQLCipher. " +
            "Encryption would silently be a no-op. Check that SQLitePCLRaw.bundle_e_sqlcipher is referenced.");
    }

    [Fact]
    public void Initialize_WrongPassword_ProducesDifferentEncryption()
    {
        // Diagnostic: prove SQLCipher is at least using the key by showing
        // that two different passwords produce different on-disk bytes for
        // the same logical content. If this passes but the wrong-password
        // test fails, the issue is detection, not encryption.
        const string password1 = "Password1";
        const string password2 = "Password2";
        var db1 = Path.Combine(_testDir, "p1.db");
        var db2 = Path.Combine(_testDir, "p2.db");

        using (var b1 = new SqliteBackend()) b1.Initialize(db1, password1.AsSpan());
        using (var b2 = new SqliteBackend()) b2.Initialize(db2, password2.AsSpan());

        var bytes1 = File.ReadAllBytes(db1);
        var bytes2 = File.ReadAllBytes(db2);

        Assert.Equal(bytes1.Length, bytes2.Length);
        // First 16 bytes are the SQLCipher salt (random per-DB, so they'll
        // differ regardless of password). Compare bytes 16..32 which are
        // page-1 header content - encrypted, so they must differ when keys
        // differ.
        Assert.False(bytes1.AsSpan(16, 16).SequenceEqual(bytes2.AsSpan(16, 16)),
            "Page-1 ciphertext is identical between two passwords - encryption is not actually keyed");
    }

    [Fact]
    public void Initialize_ReopenWithWrongPassword_ThrowsInvalidPassword()
    {
        // Arrange
        const string correctPassword = "CorrectPassword123!";
        const string wrongPassword = "WrongPassword456!";

        using (var first = new SqliteBackend())
        {
            first.Initialize(_dbPath, correctPassword.AsSpan());
            // Sanity check: confirm SQLCipher is actually doing the encryption.
            // Without this the wrong-password test could pass for the wrong
            // reason (plain SQLite would happily open any byte stream as a DB).
            Assert.False(string.IsNullOrEmpty(first.ReadSqlcipherVersion()));
        }

        // Act + Assert: SQLCipher must reject the wrong key on first read
        // rather than silently returning an empty DB.
        using var second = new SqliteBackend();
        var ex = Record.Exception(() =>
            second.Initialize(_dbPath, wrongPassword.AsSpan()));

        Assert.NotNull(ex);
        // If this fires it tells us what we actually got vs what we expected.
        Assert.True(ex is InvalidPasswordException,
            $"Expected InvalidPasswordException, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void Initialize_ManyDistinctWrongPasswords_AlwaysThrowInvalidPassword()
    {
        // Regression for the "SQLite Error 11: database disk image is malformed"
        // unlock-screen failure observed by the tester after the B47 LiteDB-probe
        // removal stopped masking the underlying classifier gap.
        //
        // SQLCipher's wrong-key surface is non-deterministic across passwords:
        // the page-1 decrypt produces uniform-random-looking bytes, and the
        // sqlite_master probe in OpenAndUnlockCore can therefore hit any of
        //   * SqliteException(SQLITE_NOTADB = 26) -- "file is not a database"
        //   * SqliteException(SQLITE_CORRUPT = 11) -- "database disk image is
        //     malformed" (was the unmapped surface; now mapped)
        //   * OverflowException / ArgumentOutOfRangeException / IndexOutOfRangeException
        //     -- garbage header values overflow the M.D.Sqlite parsing path
        //
        // All four shapes mean "wrong password" at unlock time. Any path that
        // leaks the raw exception sends the user to the password screen with a
        // baffling error string. Running 50 distinct wrong passwords against the
        // same SQLCipher database is a probabilistically-saturated cover of
        // every garbage-decrypt surface; pre-fix this test failed with the
        // SqliteException(11) leak in well under 50 iterations on typical
        // hardware.
        const string correctPassword = "CorrectClassifierProbe123!";
        using (var first = new SqliteBackend())
        {
            first.Initialize(_dbPath, correctPassword.AsSpan());
        }

        var leaks = new List<(int attempt, string type, string message)>();
        for (var i = 0; i < 50; i++)
        {
            var wrong = $"wrong-classifier-probe-{i}-{Guid.NewGuid():N}";
            using var backend = new SqliteBackend();
            var ex = Record.Exception(() => backend.Initialize(_dbPath, wrong.AsSpan()));
            if (ex is null)
            {
                leaks.Add((i, "<no exception>", "wrong password silently accepted"));
                continue;
            }
            if (ex is not InvalidPasswordException)
            {
                leaks.Add((i, ex.GetType().Name, ex.Message));
            }
        }

        Assert.True(leaks.Count == 0,
            "Wrong-password classifier let non-InvalidPasswordException shapes leak: " +
            string.Join(" | ", leaks.Select(l => $"#{l.attempt} {l.type}: {l.message}")));
    }

    [Fact]
    public void GetPendingChanges_WithIntMaxValueBatchSize_DoesNotOom()
    {
        // B17 regression: pre-fix, GetPendingChanges(int.MaxValue) tried
        // to allocate List<FileChangeEvent>(2147483647) which throws
        // OutOfMemoryException because List<T> pre-allocates an array
        // of the requested capacity. Real bug observed by tester:
        // CleanupStalePendingChanges called this path during the unlock
        // flow with int.MaxValue as a "no limit" sentinel and OOM'd
        // even on a machine with 34 GB free physical RAM (the failure
        // is per-allocation contiguous block availability, not absolute
        // memory). See B15 stack trace.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "PendingPassword123!".AsSpan());

        // Empty pending_changes table is the steady-state for an
        // unlock; the bug fires regardless of row count because the
        // failure is the pre-allocation, not the read.
        var result = backend.GetPendingChanges(int.MaxValue);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParallelSaveBackedUpFile_DoesNotProduceNestedTransactionOrRaceErrors()
    {
        // B18 regression: drive the same code path the BackupOrchestrator's
        // Parallel.ForEachAsync loop hits at MaxParallelFileBackups=8.
        // Pre-B18 the InWriteLock helper relied on the OUTER null-check
        // staying valid for the duration of the locked section, which is
        // false if a racing Close() ran between the check and
        // EnterWriteLock. The lock itself already serialised the
        // BeginTransaction calls (proven by the architecture review), so
        // the only NEW failure mode this test detects is a concurrent
        // backup workload OOM-ing or throwing nested-transaction errors
        // under heavy contention. With 8 workers x 50 files each = 400
        // SaveBackedUpFile calls in flight, any architectural mistake in
        // the lock handling surfaces here.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "ParallelSavePassword123!".AsSpan());

        const int workerCount = 8;
        const int filesPerWorker = 50;
        var workers = Enumerable.Range(0, workerCount).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < filesPerWorker; i++)
                {
                    backend.SaveBackedUpFile(new BackedUpFile
                    {
                        LocalPath = $@"C:\test\worker{workerId}\file{i}.bin",
                        BlobName = $"blob/{workerId}/{i}",
                        FileSize = 1024 * (i + 1),
                        LastModified = DateTime.UtcNow,
                        FileHash = $"hash-{workerId}-{i}",
                        Status = BackupStatus.Completed,
                        BackedUpAt = DateTime.UtcNow,
                        Chunks =
                        {
                            new ChunkInfo
                            {
                                Index = 0,
                                Offset = 0,
                                Length = 1024,
                                Hash = $"chunk-{workerId}-{i}",
                                BlobName = $"chunks/{workerId}/{i}",
                            },
                        },
                    });
                }
            })).ToArray();

        await Task.WhenAll(workers);

        // Sanity: every file landed.
        var all = backend.GetAllBackedUpFiles();
        Assert.Equal(workerCount * filesPerWorker, all.Count);
    }

    [Fact]
    public async Task Close_DuringParallelSaveBackedUpFile_DoesNotNullRefOrThrowFromCloseSide()
    {
        // B18 regression: pre-B18 Close() did NOT acquire the write lock,
        // so a racing SaveBackedUpFile could deref a null _connection
        // between its outer null-check and the inner BeginTransaction --
        // producing an opaque NullReferenceException with no
        // diagnostic context. Post-B18 either:
        //   (a) the writer finishes its transaction first and the
        //       Close acquires the lock cleanly, OR
        //   (b) the Close acquires first and the writer sees the
        //       null _connection through the InWriteLock post-lock
        //       re-check, surfacing a typed
        //       InvalidOperationException("Backend was closed before
        //       this writer could run.").
        // Either outcome is acceptable. The forbidden outcomes are
        // NullReferenceException and ObjectDisposedException leaking
        // out of either side.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "CloseRacePassword123!".AsSpan());

        var writerExceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        // 4 writer tasks racing the Close on a separate thread.
        var writers = Enumerable.Range(0, 4).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    try
                    {
                        backend.SaveBackedUpFile(new BackedUpFile
                        {
                            LocalPath = $@"C:\race\w{workerId}\f{i}.bin",
                            BlobName = $"blob/{workerId}/{i}",
                            FileSize = 100,
                            LastModified = DateTime.UtcNow,
                            FileHash = $"h-{workerId}-{i}",
                            Status = BackupStatus.Completed,
                            BackedUpAt = DateTime.UtcNow,
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        // Acceptable: hit the "backend closed" or
                        // "not initialized" guard after Close.
                    }
                    catch (Exception ex)
                    {
                        writerExceptions.Enqueue(ex);
                    }
                }
            })).ToArray();

        // Give the writers a head start, then close concurrently.
        await Task.Delay(5);
        backend.Close();

        await Task.WhenAll(writers);

        // No NRE / ObjectDisposedException should escape.
        Assert.True(writerExceptions.IsEmpty,
            "Writers leaked unexpected exceptions: " +
            string.Join(" | ", writerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}")));
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotProduceNestedTransactionOrDriverCorruption()
    {
        // B23 regression: drive the same code path the production
        // BackupOrchestrator hits when its parallel backup loop fans
        // out ChunkIndexService.AddReference to N workers. Each worker
        // alternates between SaveChunkIndexEntry (writer) and
        // GetChunkIndexEntry / GetReferencingFilesForChunk (readers)
        // on the SAME shared SqliteConnection.
        //
        // Pre-B23 the backend serialized only writes; reads were
        // documented as "WAL allows concurrent readers" -- which is
        // true for readers on DIFFERENT connections but FALSE for the
        // single shared connection this backend uses. Production
        // telemetry showed two failure shapes:
        //   1. SQLite Error 1: "cannot start a transaction within a
        //      transaction" -- a reader's open SqliteDataReader
        //      held an implicit transaction that prevented a writer
        //      thread from issuing BEGIN.
        //   2. ArgumentOutOfRangeException ("Index was out of range.
        //      Must be non-negative and less than the size of the
        //      collection. (Parameter 'index')") -- two threads
        //      doing CreateCommand/Dispose corrupted M.D.Sqlite's
        //      internal command-tracker List<>.
        // Both shapes were observed in a single 1000-file production
        // backup run.
        //
        // This test exercises both seams. Writers do an upsert per
        // iteration (BeginTransaction internally via SaveChunkIndexEntry's
        // ON CONFLICT); readers do GetChunkIndexEntry and
        // GetReferencingFilesForChunk -- ExecuteReader paths that
        // pre-B23 were unprotected. Any leaked exception fails the test.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "MixedReadWritePassword123!".AsSpan());

        // Seed a known set of chunks so readers always have rows to read.
        const int chunkCount = 50;
        var seed = new List<ChunkIndexEntry>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            seed.Add(new ChunkIndexEntry
            {
                ChunkHash = $"seed-{i:000}",
                FirstUploadedAt = DateTime.UtcNow,
                OriginalUploaderPath = $@"C:\seed\file{i}.bin",
                SizeBytes = 1024 * (i + 1),
                ReferenceCount = 1,
                CurrentTier = StorageTier.Hot,
                LastVerifiedAt = DateTime.UtcNow,
            });
        }
        backend.BulkInsertChunkIndexEntries(seed);

        const int workerCount = 8;
        const int iterationsPerWorker = 200;
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var workers = Enumerable.Range(0, workerCount).Select(workerId =>
            Task.Run(() =>
            {
                var rng = new Random(workerId * 1009);
                for (var i = 0; i < iterationsPerWorker; i++)
                {
                    try
                    {
                        // Half writes, half reads -- mixed in a tight loop
                        // so threads collide on the connection often.
                        if ((i & 1) == 0)
                        {
                            backend.SaveChunkIndexEntry(new ChunkIndexEntry
                            {
                                ChunkHash = $"w{workerId}-{i:000}",
                                FirstUploadedAt = DateTime.UtcNow,
                                OriginalUploaderPath = $@"C:\worker{workerId}\file{i}.bin",
                                SizeBytes = 4096,
                                ReferenceCount = 1,
                                CurrentTier = StorageTier.Hot,
                                LastVerifiedAt = DateTime.UtcNow,
                            });
                        }
                        else
                        {
                            var hash = $"seed-{rng.Next(chunkCount):000}";
                            _ = backend.GetChunkIndexEntry(hash);
                            _ = backend.GetReferencingFilesForChunk(hash);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }
            })).ToArray();

        await Task.WhenAll(workers);

        Assert.True(exceptions.IsEmpty,
            "Mixed read/write workload leaked exceptions (B23 regression): " +
            string.Join(" | ", exceptions.Select(e => $"{e.GetType().Name}: {e.Message}")));
    }

    [Fact]
    public void CheckDatabaseFileIntegrity_OnHealthyDatabase_ReportsBothPragmasOk()
    {
        // B44: a freshly initialised database must pass both
        // SQLCipher's per-page HMAC check and SQLite's b-tree check.
        // SQLCipher's cipher_integrity_check returns ZERO rows on success
        // (one per failing page otherwise), the opposite of stock SQLite's
        // integrity_check which always returns a single "ok" row on
        // success. The result contract that the Storage Health tab
        // depends on must reflect both shapes correctly.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "DiagnosticPassword123!".AsSpan());

        var result = backend.CheckDatabaseFileIntegrity();

        Assert.True(result.CipherOk,
            "cipher_integrity_check reported failures on a fresh DB. Got: " +
            string.Join(" | ", result.CipherIntegrityMessages));
        Assert.True(result.SqliteOk,
            "integrity_check did not return a single 'ok' row on a fresh DB. Got: " +
            string.Join(" | ", result.SqliteIntegrityMessages));
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void ReindexCorruptIndexes_OnHealthyDatabase_RefusesAndDoesNotRunReindex()
    {
        // B45: the repair path is gated on the diagnosis NOT being
        // healthy. Calling it on a fresh DB must refuse cleanly with
        // an explanatory message rather than silently reindexing
        // every healthy index. This is the contract the Storage
        // Health view's CanAttemptRepair gate relies on.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "RepairPassword123!".AsSpan());

        var diagnosis = backend.CheckDatabaseFileIntegrity();
        Assert.True(diagnosis.IsHealthy);

        var repair = backend.ReindexCorruptIndexes(diagnosis);

        Assert.False(repair.WasAttempted);
        Assert.Empty(repair.AttemptedIndexes);
        Assert.Empty(repair.SucceededIndexes);
        Assert.Empty(repair.FailedIndexes);
        Assert.Null(repair.PostRepairDiagnosis);
        Assert.Contains("healthy", repair.RefusalReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReindexCorruptIndexes_OnCipherDamage_RefusesBeforeTouchingAnyIndex()
    {
        // B45: a synthetic diagnosis with cipher failures must short-
        // circuit BEFORE any REINDEX runs. We do not need to actually
        // corrupt the file; the repair API takes the diagnosis as
        // input so we can construct the failure shape directly.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "RepairPassword123!".AsSpan());

        var fakeCipherFailure = new DatabaseFileIntegrityResult(
            CipherIntegrityMessages: new[] { "page 7 corrupted" },
            SqliteIntegrityMessages: System.Array.Empty<string>());

        var repair = backend.ReindexCorruptIndexes(fakeCipherFailure);

        Assert.False(repair.WasAttempted);
        Assert.Contains("cipher", repair.RefusalReason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REINDEX cannot fix", repair.RefusalReason, System.StringComparison.Ordinal);
    }
}
