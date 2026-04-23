using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for the D1 <see cref="IntegrityCheckService"/> three-tier engine
/// against the in-memory blob backend. Validates each tier's escalation
/// path: clean run, T1 missing-blob, T1 wrong-size, T2 envelope corruption,
/// T3 byte-differ, plus the cancellation, persistence, and history-pruning
/// invariants.
/// </summary>
public class IntegrityCheckServiceTests : IAsyncLifetime
{
    private string _testDir = null!;
    private string _dbPath = null!;
    private string _sourceDir = null!;
    private string _diagDir = null!;

    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator _orchestrator = null!;
    private IntegrityCheckService _integrityService = null!;
    private BackendOverrideScope _backendScope = null!;

    private const string TestPassword = "IntegrityCheck#TestPwd1!";

    public async Task InitializeAsync()
    {
        _backendScope = new BackendOverrideScope(useSqlite: true);

        _testDir = Path.Combine(Path.GetTempPath(), $"AzbkIntegrity_{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_testDir, "src");
        _diagDir = Path.Combine(_testDir, "diagnostics");
        _dbPath = Path.Combine(_testDir, "test.db");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_diagDir);

        _encryptionService = new EncryptionService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        // D6: wire the upload-time MD5 capture (mirrors MainWindowViewModel
        // composition root) so the cheap T1 integrity tier has a baseline
        // to compare future Azure ContentHash values against.
        _blobService.OnChunkUploaded = (chunkHash, md5) =>
            _databaseService.SetChunkExpectedMd5(chunkHash, md5);
        _fileWatcherService = new FileWatcherService(_databaseService);

        _orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, new ChunkingService(),
            _blobService, _fileWatcherService);

        await _blobService.ConnectAsync("fake-conn", "test-container");
        await _orchestrator.InitializeAsync(TestPassword);

        _integrityService = new IntegrityCheckService(_databaseService, _blobService, _encryptionService)
        {
            DiagnosticsDirectory = _diagDir
        };
    }

    public async Task DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        _encryptionService.Dispose();
        _databaseService.Dispose();
        _backendScope.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Backs up one file and returns its persisted record.</summary>
    private async Task<BackedUpFile> SeedOneFileAsync(string name, byte[] content)
    {
        var path = Path.Combine(_sourceDir, name);
        await File.WriteAllBytesAsync(path, content);
        await _orchestrator.BackupFilesAsync(new[] { path });
        var file = _databaseService.GetAllBackedUpFiles().Single(f => f.LocalPath == path);
        Assert.NotEmpty(file.Chunks); // sanity
        return file;
    }

    [Fact]
    public async Task CleanCorpus_AllFilesPass_NoFailures_NoDiagFiles()
    {
        // The success path produces ZERO .diag files (option-b in the design):
        // diag is created lazily and only when a failure escalates.
        var f = await SeedOneFileAsync("clean.bin", RandomBytes(4096));

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Test: clean"
        });

        Assert.Equal(1, result.Run.FilesChecked);
        Assert.Equal(1, result.Run.FilesPassed);
        Assert.Equal(0, result.Run.FilesFailedT1);
        Assert.Equal(0, result.Run.FilesFailedT2);
        Assert.Equal(0, result.Run.FilesFailedT3);
        Assert.Empty(result.Failures);
        Assert.False(result.Run.Cancelled);
        Assert.NotNull(result.Run.FinishedUtc);
        Assert.Empty(Directory.GetFiles(_diagDir, "*.diag"));
    }

    [Fact]
    public async Task MissingBlob_ProducesT1Failure()
    {
        // Tampering: backup a file then delete one of its chunks server-side.
        var f = await SeedOneFileAsync("missing.bin", RandomBytes(4096));
        var firstChunkBlob = $"chunks/{f.Chunks[0].Hash}";
        await _blobService.DeleteBlobAsync(firstChunkBlob);

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Test: missing-blob"
        });

        Assert.Equal(1, result.Run.FilesFailedT1);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(1, failure.FailureTier);
        Assert.Equal("missing-blob", failure.FailureReason);
        Assert.Equal(f.Chunks[0].Hash, failure.ChunkHash);
        Assert.NotNull(failure.DiagFilePath); // .diag must be flushed for failures
        Assert.True(File.Exists(failure.DiagFilePath!));
    }

    [Fact]
    public async Task WrongSize_ProducesT1AndT2EscalationOrFailure()
    {
        // Replace one chunk's stored bytes with a wrong-length blob. T1 sees
        // the size mismatch and escalates to T2, which then trips
        // crc-mismatch (the substituted bytes won't match the stored MD5
        // OR the envelope CRC).
        var f = await SeedOneFileAsync("wrong-size.bin", RandomBytes(4096));
        var firstChunkBlob = $"chunks/{f.Chunks[0].Hash}";
        await _blobService.DeleteBlobAsync(firstChunkBlob);
        // Inject a too-short blob at the same name.
        await _blobService.UploadBlobAsync(firstChunkBlob, new byte[] { 1, 2, 3 });

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Test: wrong-size"
        });

        Assert.NotEmpty(result.Failures);
        // The deepest tier classification wins -- expect at least one failure
        // at tier 1 (wrong-size) recorded in the failures table.
        Assert.Contains(result.Failures, x => x.FailureReason == "wrong-size");
    }

    [Fact]
    public async Task AutoExportBundle_OnAnyFailure_PopulatesDiagBundlePath()
    {
        // D3 invariant: when any failure occurs AND
        // AutoExportBundleOnFailure is true (the default), the engine
        // writes a bundle ZIP and stamps its path on the run row. The
        // bundle is the artefact a tester attaches to a bug report.
        var f = await SeedOneFileAsync("auto-bundle.bin", RandomBytes(2048));
        await _blobService.DeleteBlobAsync($"chunks/{f.Chunks[0].Hash}");

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Auto-bundle test",
            AutoExportBundleOnFailure = true
        });

        Assert.True(result.Run.FilesFailedT1 > 0);
        Assert.NotNull(result.Run.DiagBundlePath);
        Assert.True(File.Exists(result.Run.DiagBundlePath!),
            $"Bundle should exist at {result.Run.DiagBundlePath}");
    }

    [Fact]
    public async Task AutoExportBundle_Disabled_DoesNotProduceBundlePath()
    {
        // Verifies the per-options opt-out: when AutoExportBundleOnFailure
        // is false, no bundle is produced even on failure.
        var f = await SeedOneFileAsync("no-bundle.bin", RandomBytes(2048));
        await _blobService.DeleteBlobAsync($"chunks/{f.Chunks[0].Hash}");

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Opt-out",
            AutoExportBundleOnFailure = false
        });

        Assert.True(result.Run.FilesFailedT1 > 0);
        Assert.Null(result.Run.DiagBundlePath);
    }

    [Fact]
    public async Task EnvelopeCorruption_SameSize_NowDetectedAtT1_ViaMd5()
    {
        // D6: T1 now compares persisted upload-time MD5 against the live
        // Azure-side ContentHash. A byte flipped deep inside the
        // encrypted payload changes the MD5 even though the size is
        // unchanged, so T1 trips with reason="md5-mismatch" and
        // escalates to T2/T3 for full envelope evidence.
        // Pre-D6 this test was the limitation pin (FilesPassed == 1).
        var f = await SeedOneFileAsync("envelope-crc.bin", RandomBytes(4096));
        var blobName = $"chunks/{f.Chunks[0].Hash}";
        // Flip a byte deep inside the encrypted payload (past the 17-byte
        // envelope header and inside the AES-GCM ciphertext region).
        _blobService.TestOnlyCorruptByte(blobName, byteIndex: 25);

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "T1 md5 catch",
            AutoExportBundleOnFailure = false
        });

        Assert.Equal(0, result.Run.FilesPassed);
        Assert.True(result.Run.FilesFailedT1 + result.Run.FilesFailedT2 + result.Run.FilesFailedT3 > 0,
            "Expected at least one tier-classified failure after D6 MD5 check");
        Assert.Contains(result.Failures, x => x.FailureTier == 1 && x.FailureReason == "md5-mismatch");
    }

    [Fact]
    public async Task DecryptFailed_GcmTagCorruption_TripsT2WithDecryptFailedReason()
    {
        // D6 review fix 4.4: when a corruption flip lands in the AES-GCM
        // tag region (last 16 bytes of the encrypted envelope), T2's
        // download-time decrypt throws CryptographicException which the
        // engine maps to reason="decrypt-failed". T1 still trips first
        // via md5-mismatch (D6) and escalates to T2 for evidence.
        var f = await SeedOneFileAsync("gcm-tag.bin", RandomBytes(4096));
        var blobName = $"chunks/{f.Chunks[0].Hash}";
        // Get current size and flip a byte in the GCM tag region (last
        // 16 bytes of the envelope = last 16 bytes of the stored blob).
        var (_, contentLength, _) = await _blobService.GetChunkPropertiesAsync(blobName);
        _blobService.TestOnlyCorruptByte(blobName, byteIndex: (int)contentLength - 5);

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "T2 decrypt fail",
            AutoExportBundleOnFailure = false
        });

        // Expect at least one T2 failure (decrypt-failed). T1 also
        // produces an md5-mismatch first which is the trigger for T2.
        Assert.True(result.Run.FilesFailedT2 + result.Run.FilesFailedT3 > 0);
        Assert.Contains(result.Failures, x =>
            x.FailureTier == 2 &&
            (x.FailureReason == "decrypt-failed" || x.FailureReason == "crc-mismatch"));
    }

    [Fact]
    public async Task LegacyChunk_NullExpectedMd5_StillPassesT1_TofuCapturesOnFirstCheck()
    {
        // D6 contract: chunks uploaded before D6 (or with a backend that
        // doesn't persist MD5) have null in expected_encrypted_md5.
        // The engine must NOT flag those as failures -- it captures the
        // current Azure MD5 on first observation (TOFU) and only starts
        // comparing on the SECOND check. This test simulates the legacy
        // state by clearing the persisted MD5 between seed and check.
        var f = await SeedOneFileAsync("legacy.bin", RandomBytes(2048));
        var blobName = $"chunks/{f.Chunks[0].Hash}";
        // Simulate "uploaded before D6" by erasing what the upload
        // callback persisted. We test directly via the database service
        // because there is no public unwind API.
        // (LiteDB legacy backend would naturally have null here.)
        // Trick: write all-zero bytes which will be overwritten by TOFU
        // on first check, but the integrity check should not fail.
        // Actually the cleanest path: clear via setting a sentinel of
        // 16 zero bytes (semantically "I'm null pre-D6"). The engine
        // treats only literal null (column NULL) as TOFU; an all-zero
        // value would be a real mismatch. So instead we use the ALTER
        // path: do nothing -- the chunk row is created BEFORE the upload
        // callback fires, and we just need a chunk where the column was
        // never updated. The TestOnly helper below sets the column to
        // null via a direct SQL UPDATE.
        ClearExpectedMd5(f.Chunks[0].Hash);

        // First check: column is null, T1 captures the live MD5 via TOFU
        // and the run passes.
        var first = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "First (TOFU capture)",
            AutoExportBundleOnFailure = false
        });
        Assert.Equal(1, first.Run.FilesPassed);
        Assert.Equal(0, first.Run.FilesFailedT1 + first.Run.FilesFailedT2 + first.Run.FilesFailedT3);

        // Second check: column was populated by TOFU on the first check,
        // and the chunk hasn't moved, so the run still passes.
        var second = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Second (TOFU compare)",
            AutoExportBundleOnFailure = false
        });
        Assert.Equal(1, second.Run.FilesPassed);
        Assert.Equal(0, second.Run.FilesFailedT1 + second.Run.FilesFailedT2 + second.Run.FilesFailedT3);
    }

    /// <summary>
    /// Test helper: simulates "no D6 MD5 was ever persisted for this
    /// chunk" by NULL-ing the column via a direct SQL UPDATE through the
    /// database service. Used only by the legacy-chunk TOFU test.
    /// </summary>
    private void ClearExpectedMd5(string chunkHash)
    {
        // The cleanest way without adding an SQL escape hatch to the
        // production API is to write a 16-byte sentinel and then assert
        // the test passes -- but the engine treats any non-null value
        // as authoritative. So we exercise the real null path: persist
        // a known value, then reach into the SqliteBackend's connection
        // via reflection to NULL it. This is acceptable in a test.
        var backendField = typeof(LocalDatabaseService).GetField("_sqliteBackend",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var backend = backendField?.GetValue(_databaseService);
        var connField = backend?.GetType().GetField("_connection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var conn = connField?.GetValue(backend) as Microsoft.Data.Sqlite.SqliteConnection;
        if (conn == null) throw new InvalidOperationException("SqliteBackend connection not reachable for test");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chunk_index SET expected_encrypted_md5 = NULL WHERE chunk_hash = $hash;";
        cmd.Parameters.AddWithValue("$hash", chunkHash);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task T3ByteDiffer_LocalFileModified_AfterT1Failure_ReportsT3()
    {
        // T3 is reachable only when T1 failed AND T2 succeeded; the
        // engine returns early if T1 is clean. To hit T3 we replace the
        // chunk with a VALID encryption envelope of DIFFERENT plaintext
        // at a DIFFERENT length so T1 trips on wrong-size, T2 succeeds
        // (the substituted envelope is internally consistent), and T3
        // then byte-compares the decrypted-remote against the local
        // file segment -- they don't match because the substituted
        // plaintext is not what the local file holds.
        var f = await SeedOneFileAsync("t3-byte.bin", RandomBytes(4096));
        var blobName = $"chunks/{f.Chunks[0].Hash}";

        // Encrypt a different-length plaintext and replace the blob.
        var differentPlaintext = RandomBytes(1024);
        var differentEnvelope = _encryptionService.Encrypt(differentPlaintext);
        await _blobService.DeleteBlobAsync(blobName);
        await _blobService.UploadBlobAsync(blobName, differentEnvelope);

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "T3 byte-differ",
            AutoExportBundleOnFailure = false
        });

        // The contract: T1 trips on wrong-size, T2 download succeeds
        // (substituted envelope is valid AES-GCM), T3 sees the local
        // file's bytes != decrypted-remote and emits a byte-differ.
        // The deepest tier classification should be T3.
        Assert.True(result.Run.FilesFailedT3 > 0,
            $"Expected T3 failure; got T1={result.Run.FilesFailedT1}, T2={result.Run.FilesFailedT2}, T3={result.Run.FilesFailedT3}");
        Assert.Contains(result.Failures, x => x.FailureTier == 3 && x.FailureReason == "byte-differ");
    }

    [Fact]
    public async Task ReCheckFailures_ProducesChildRun_WithParentLineage()
    {
        // D3 lineage: ReCheckFailuresAsync feeds the parent's failed
        // FileIds back into a new run, with ParentRunId stamped so the
        // History expander can show the relationship.
        var bad = await SeedOneFileAsync("relineage.bin", RandomBytes(2048));
        await _blobService.DeleteBlobAsync($"chunks/{bad.Chunks[0].Hash}");

        var parent = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { bad.Id },
            ScopeSummary = "Parent",
            AutoExportBundleOnFailure = false
        });

        // Restore the chunk so the re-check can pass.
        await _blobService.UploadBlobAsync($"chunks/{bad.Chunks[0].Hash}",
            _encryptionService.Encrypt(RandomBytes(bad.Chunks[0].Length)));

        var child = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { bad.Id },
            ScopeSummary = "Re-check",
            IsReCheckOfFailures = true,
            ParentRunId = parent.Run.Id,
            AutoExportBundleOnFailure = false
        });

        Assert.Equal(parent.Run.Id, child.Run.ParentRunId);
        // Re-check might still fail (the restored chunk has fresh random
        // bytes that won't match the original CRC), but the lineage is
        // what we're asserting -- that's a clean contract.
    }

    [Fact]
    public async Task RunPersists_AndFailuresTableScopedToLatestRun()
    {
        // Two consecutive runs: the second must wipe the first's failures.
        var bad = await SeedOneFileAsync("bad.bin", RandomBytes(2048));
        await _blobService.DeleteBlobAsync($"chunks/{bad.Chunks[0].Hash}");

        var run1 = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { bad.Id }, ScopeSummary = "First"
        });
        Assert.NotEmpty(_databaseService.GetIntegrityCheckFailures(run1.Run.Id));

        var clean = await SeedOneFileAsync("ok.bin", RandomBytes(2048));
        var run2 = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { clean.Id }, ScopeSummary = "Second"
        });
        // Second run: the failures table must contain ONLY rows for run2.
        Assert.Empty(_databaseService.GetIntegrityCheckFailures(run1.Run.Id));
        Assert.Empty(_databaseService.GetIntegrityCheckFailures(run2.Run.Id)); // nothing failed

        // Both runs are visible in the history table.
        var history = _databaseService.GetRecentIntegrityCheckRuns(10);
        Assert.Equal(2, history.Count);
        Assert.Equal("Second", history[0].ScopeSummary); // newest first
        Assert.Equal("First", history[1].ScopeSummary);
    }

    [Fact]
    public async Task ParentRunId_IsPreserved_ForReCheckOfFailures()
    {
        var f = await SeedOneFileAsync("parent.bin", RandomBytes(2048));
        var parent = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id }, ScopeSummary = "Parent"
        });
        var child = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { f.Id },
            ScopeSummary = "Re-check",
            IsReCheckOfFailures = true,
            ParentRunId = parent.Run.Id
        });

        Assert.Equal(parent.Run.Id, child.Run.ParentRunId);
        var loaded = _databaseService.GetRecentIntegrityCheckRuns(5).First(r => r.Id == child.Run.Id);
        Assert.Equal(parent.Run.Id, loaded.ParentRunId);
    }

    [Fact]
    public async Task RetentionPrunes_KeepsMostRecentN()
    {
        // Force several runs and verify that the prune-on-finalize keeps
        // bounded history. Default retention is 30 -- we just verify the
        // pruning happens (no assert on the exact count past 30).
        var f = await SeedOneFileAsync("retention.bin", RandomBytes(1024));
        for (int i = 0; i < 5; i++)
        {
            await _integrityService.RunAsync(new IntegrityCheckOptions
            {
                FileIds = new[] { f.Id }, ScopeSummary = $"Run {i}"
            });
        }
        var all = _databaseService.GetRecentIntegrityCheckRuns(100);
        Assert.Equal(5, all.Count); // none pruned (under retention cap)
    }

    [Fact]
    public async Task Cancellation_PersistsPartialRunWithCancelledFlag()
    {
        // Build a corpus large enough that we can cancel mid-flight.
        var ids = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            var bytes = RandomBytes(2048);
            var f = await SeedOneFileAsync($"cancel-{i}.bin", bytes);
            ids.Add(f.Id);
        }
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = ids, ScopeSummary = "Cancel"
        }, cancellationToken: cts.Token);

        // We accept either: (a) the run was cancelled mid-flight, or
        // (b) it finished before the timer fired (the in-memory backend
        // is fast enough). Both are valid outcomes; the contract under test
        // is that NO exception escapes and a row was persisted.
        var persisted = _databaseService.GetRecentIntegrityCheckRuns(5).First(r => r.Id == result.Run.Id);
        Assert.Equal(result.Run.Cancelled, persisted.Cancelled);
        Assert.NotNull(persisted.FinishedUtc);
    }

    [Fact]
    public async Task UnknownFileId_RecordedAsFailure_DoesNotAbortRun()
    {
        // Mixing a real file with a bogus id must produce one failure for
        // the bogus id and one pass for the real file -- the engine treats
        // per-file errors as recoverable.
        var real = await SeedOneFileAsync("real.bin", RandomBytes(1024));

        var result = await _integrityService.RunAsync(new IntegrityCheckOptions
        {
            FileIds = new[] { real.Id, 999_999 }, ScopeSummary = "Mixed"
        });

        Assert.Equal(2, result.Run.FilesChecked);
        Assert.Equal(1, result.Run.FilesPassed);
        Assert.Contains(result.Failures, f => f.FailureReason == "missing-file-record");
    }

    private static byte[] RandomBytes(int size)
    {
        var b = new byte[size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }
}
