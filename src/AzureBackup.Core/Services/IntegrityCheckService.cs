using System.Buffers;
using System.Security.Cryptography;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Three-tier post-backup data integrity check (D1).
/// </summary>
/// <remarks>
/// <para>Tiers (each escalates only on failure of the prior tier):</para>
/// <list type="number">
///   <item><b>T1 -- structural HEAD:</b> for each chunk, fetch
///     <c>(Exists, ContentLength, ContentHash)</c> via
///     <see cref="IBlobStorageService.GetChunkPropertiesAsync"/>. Compare
///     existence and length-vs-expected (chunk plaintext length +
///     <see cref="EncryptionService.EncryptionOverhead"/>). Cost:
///     ~1 KB / chunk over the wire, no body bytes.</item>
///   <item><b>T2 -- full blob:</b> on T1 fail, download the full encrypted
///     blob via <see cref="IBlobStorageService.DownloadChunkAsync"/>, which
///     internally runs envelope CRC + AES-GCM tag check via
///     <c>VerifyDownloadIntegrity</c> and emits the X1b
///     <c>[CRC FAIL]</c> diag with envelope head/tail/version.</item>
///   <item><b>T3 -- byte-for-byte:</b> on T2 success-but-still-suspect (or
///     direct caller request), re-read the local file's
///     <c>chunkInfo.Length</c> bytes at <c>chunkInfo.Offset</c> and pass
///     to <see cref="IBlobStorageService.VerifyChunkIntegrityAsync"/>
///     which decrypts the remote chunk and runs constant-time compare.</item>
/// </list>
/// <para>
/// The engine emits a per-file <see cref="FileOperationDiagnostics"/> .diag
/// for every failure (any tier) plus a single run-summary .diag at op end.
/// Per-op metrics flow through <see cref="ThroughputMetrics"/> using
/// <c>operation = "integrity-check"</c> so the existing X2 CrcFailCount
/// counter is captured automatically and decision records justify any
/// tier escalation.
/// </para>
/// </remarks>
public sealed class IntegrityCheckService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;

    /// <summary>
    /// HEAD-request concurrency cap (D5 review fix 3.2). 16 is comfortably
    /// below Azure's 5 K transactions/sec/partition limit and large enough
    /// that T1 stays cheap on a normal corpus. Single instance shared
    /// across the engine so the EFFECTIVE cap is 16, not 16 x file-level
    /// parallelism (which pre-D5 multiplied to 128).
    /// </summary>
    private const int T1Concurrency = 16;

    /// <summary>
    /// Full-blob download concurrency cap (D5 review fix 3.3). T2/T3
    /// pull body bytes so the bottleneck is bandwidth, not request rate.
    /// 4 keeps a healthy run cheap and a heavily-corrupted run from
    /// saturating the link.
    /// </summary>
    private const int T2Concurrency = 4;

    /// <summary>How many integrity-check runs to keep in the history table.</summary>
    private const int RunRetention = 30;

    /// <summary>
    /// Optional <see cref="ThroughputMetrics"/> sink. When non-null the engine
    /// emits a context record at start, decision records on tier escalations,
    /// and an op record at end (with per-op CRC-fail delta from X2).
    /// </summary>
    public ThroughputMetrics? Metrics { get; set; }

    /// <summary>
    /// Optional <see cref="DiagnosticBundleExporter"/> directory. When
    /// non-null AND the run produces any failures AND
    /// <see cref="IntegrityCheckOptions.AutoExportBundleOnFailure"/> is
    /// true, the engine writes a bundle ZIP and stores its path on the run row.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }

    /// <summary>Optional <see cref="CrashSafeLogger"/> session id for log correlation.</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Event for per-line debug log forwarding to <c>CrashSafeLogger</c>.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    private void Log(string message)
    {
        DiagnosticLog?.Invoke(this, $"[IntegrityCheck] {message}");
    }

    public IntegrityCheckService(
        LocalDatabaseService databaseService,
        IBlobStorageService blobService,
        EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(encryptionService);
        _databaseService = databaseService;
        _blobService = blobService;
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Runs the three-tier check against the files identified by
    /// <see cref="IntegrityCheckOptions.FileIds"/>. Returns the persisted
    /// run row plus an in-memory list of failures.
    /// </summary>
    public async Task<IntegrityCheckResult> RunAsync(
        IntegrityCheckOptions options,
        IProgress<IntegrityCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_databaseService.IntegrityCheckSupported)
            throw new NotSupportedException("Integrity check requires the SQLite backend (see LocalDatabaseService).");
        // D7 review fix 1.12: programmer error -- a 0-file run is
        // ambiguous (would record a "passing" run row that summarises
        // nothing). The UI guards against this via CanRunCheck but the
        // engine should reject it independently.
        if (options.FileIds.Count == 0)
            throw new ArgumentException("FileIds must be non-empty.", nameof(options));

        // --- Setup ---
        var startedUtc = DateTime.UtcNow;
        var run = new IntegrityCheckRun
        {
            StartedUtc = startedUtc,
            SessionId = SessionId,
            ScopeSummary = options.ScopeSummary,
            ParentRunId = options.ParentRunId
        };
        var runId = _databaseService.InsertIntegrityCheckRun(run);
        // The new run's failures table starts empty; stale rows from prior
        // runs go away here so the "Failures (latest run)" UI is bounded.
        _databaseService.DeleteIntegrityCheckFailuresExcept(runId);

        var crcFailStart = _blobService.TotalCrcFailures;

        Metrics?.RecordContext("integrity-check", memoryBudgetMb: 0, memoryBudgetEnabled: false);
        Metrics?.RecordDecision("integrity-check-start", new Dictionary<string, object?>
        {
            ["files"] = options.FileIds.Count,
            ["scope"] = options.ScopeSummary,
            ["t1Concurrency"] = T1Concurrency,
            ["isReCheck"] = options.IsReCheckOfFailures,
            ["parentRunId"] = options.ParentRunId
        });

        var failures = new List<IntegrityCheckFailure>();
        var failuresLock = new object();
        int filesPassed = 0, filesFailedT1 = 0, filesFailedT2 = 0, filesFailedT3 = 0, filesWarning = 0;
        int filesProcessed = 0;
        var totalFiles = options.FileIds.Count;

        Log($"RunAsync: starting (files={totalFiles}, scope='{options.ScopeSummary}', runId={runId})");

        // Snapshot the corpus once. Pre-D4 the engine called
        // GetAllBackedUpFiles() inside CheckOneFileAsync for EVERY file in
        // scope, decoding the entire backup catalogue per worker. For a
        // 1000-file run that is ~1000 full table scans (O(N^2) over N files
        // = O(N^3) over chunks); a 10K-file run was unusable. The map
        // below converts the lookup to O(1) per file.
        // D7 review fix: the corpus snapshot uses Task.Run with the
        // cancellation token, so a pre-cancelled token throws BEFORE we
        // reach the catch handler that sets run.Cancelled. Allocate
        // empty corpus on cancel so the run row is properly stamped.
        Dictionary<int, BackedUpFile> corpus;

        // D5 fix 3.2/3.3: single shared semaphores across the run. Pre-D5
        // each per-file worker had its own SemaphoreSlim(16) which the 8
        // file-level workers multiplied to 128 effective HEAD requests in
        // flight; T2 had no cap at all. Now T1 is bounded at 16 and T2
        // at 4 across the entire engine, regardless of file count.
        using var t1Sem = new SemaphoreSlim(T1Concurrency);
        using var t2Sem = new SemaphoreSlim(T2Concurrency);

        try
        {
            corpus = (await Task.Run(() => _databaseService.GetAllBackedUpFiles(), cancellationToken))
                .ToDictionary(f => f.Id);

            // Per-file workers. File-level parallelism (8) stays the same as
            // the rest of the app; each file's chunks are checked sequentially
            // because that's where the cancel-friendly progress reporting
            // lives. Effective HEAD/download concurrency is bounded by the
            // shared semaphores above.
            await Parallel.ForEachAsync(
                options.FileIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 8,
                    CancellationToken = cancellationToken
                },
                async (fileId, ct) =>
                {
                    var fileFailures = await CheckOneFileAsync(fileId, runId, corpus, t1Sem, t2Sem, ct);
                    var processed = Interlocked.Increment(ref filesProcessed);

                    int t1 = 0, t2 = 0, t3 = 0, warn = 0;
                    foreach (var f in fileFailures)
                    {
                        if (f.FailureTier == 1)
                        {
                            if (f.FailureReason == "size-disagreement") warn++;
                            else t1++;
                        }
                        else if (f.FailureTier == 2) t2++;
                        else if (f.FailureTier == 3) t3++;
                    }

                    // Classify the file by its deepest failure tier.
                    if (fileFailures.Count == 0)
                    {
                        Interlocked.Increment(ref filesPassed);
                    }
                    else if (t3 > 0) Interlocked.Increment(ref filesFailedT3);
                    else if (t2 > 0) Interlocked.Increment(ref filesFailedT2);
                    else if (t1 > 0) Interlocked.Increment(ref filesFailedT1);
                    else if (warn > 0) Interlocked.Increment(ref filesWarning);

                    if (fileFailures.Count > 0)
                    {
                        lock (failuresLock) failures.AddRange(fileFailures);
                        foreach (var f in fileFailures)
                        {
                            try { _databaseService.InsertIntegrityCheckFailure(f); }
                            catch (Exception ex) { Log($"InsertIntegrityCheckFailure failed: {ex.Message}"); }
                        }
                    }

                    progress?.Report(new IntegrityCheckProgress(
                        processed, totalFiles,
                        fileFailures.FirstOrDefault()?.LocalPath ?? "",
                        Volatile.Read(ref filesFailedT1),
                        Volatile.Read(ref filesFailedT2),
                        Volatile.Read(ref filesFailedT3)));
                });
        }
        catch (OperationCanceledException)
        {
            run.Cancelled = true;
            Log("RunAsync: cancelled");
        }

        // --- Finalize ---
        run.Id = runId;
        run.FinishedUtc = DateTime.UtcNow;
        run.FilesChecked = filesProcessed;
        run.FilesPassed = filesPassed;
        run.FilesFailedT1 = filesFailedT1;
        run.FilesFailedT2 = filesFailedT2;
        run.FilesFailedT3 = filesFailedT3;
        run.FilesWarning = filesWarning;

        var anyFailure = filesFailedT1 + filesFailedT2 + filesFailedT3 > 0;
        if (anyFailure && options.AutoExportBundleOnFailure && DiagnosticsDirectory is { } dir)
        {
            try
            {
                var dataDir = System.IO.Path.GetDirectoryName(dir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)) ?? dir;
                run.DiagBundlePath = DiagnosticBundleExporter.Export(dataDir, dataDir, SessionId);
                Log($"Auto-exported bundle to {run.DiagBundlePath}");
            }
            catch (Exception ex)
            {
                Log($"Bundle export failed: {ex.Message}");
            }
        }

        _databaseService.UpdateIntegrityCheckRun(run);
        _databaseService.PruneIntegrityCheckRuns(RunRetention);

        var elapsed = (run.FinishedUtc.Value - run.StartedUtc).TotalSeconds;
        Metrics?.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "integrity-check",
            Files = filesProcessed,
            Succeeded = filesPassed,
            Failed = filesFailedT1 + filesFailedT2 + filesFailedT3,
            ElapsedSeconds = elapsed,
            CrcFailCount = (int)(_blobService.TotalCrcFailures - crcFailStart)
        });

        Log($"RunAsync: complete in {elapsed:F1}s -- passed={filesPassed}, " +
            $"T1={filesFailedT1}, T2={filesFailedT2}, T3={filesFailedT3}, warn={filesWarning}, cancelled={run.Cancelled}");

        return new IntegrityCheckResult
        {
            Run = run,
            Failures = failures
        };
    }

    /// <summary>
    /// D10: one-shot backfill scan that promotes pre-D6 chunks. For
    /// every chunk whose <c>expected_encrypted_md5</c> is null, runs a
    /// T2 download + envelope verify (CRC32 + AES-GCM tag via
    /// <see cref="IBlobStorageService.DownloadChunkAsync"/>); only if
    /// the download succeeds AND the live Azure ContentHash matches
    /// what we just MD5-hashed locally do we stamp the MD5. This
    /// closes the TOFU window vulnerability where a chunk corrupt at
    /// the time of first integrity check would have its corrupt MD5
    /// captured as the "expected" value and pass forever after.
    /// </summary>
    /// <returns>
    /// A <see cref="LegacyMd5BackfillResult"/> with totals for
    /// promoted, skipped, and failed chunks. Failures are NOT a fatal
    /// error -- they leave the chunk's expected MD5 still null so the
    /// scan can be retried later.
    /// </returns>
    /// <remarks>
    /// Concurrency: T2 downloads gated by <c>T2Concurrency</c> (4) so
    /// a multi-thousand-chunk corpus does not saturate the network.
    /// Cancellation is honoured between chunks (not mid-download).
    /// Progress is reported every 10 chunks to keep UI updates cheap.
    /// </remarks>
    public async Task<LegacyMd5BackfillResult> BackfillLegacyMd5Async(
        IProgress<LegacyMd5BackfillProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_databaseService.IntegrityCheckSupported)
            throw new NotSupportedException("BackfillLegacyMd5Async requires the SQLite backend.");

        var hashes = _databaseService.GetChunkHashesWithNullExpectedMd5().ToList();
        var total = hashes.Count;
        Log($"BackfillLegacyMd5Async: starting on {total} chunk(s) with null expected MD5");

        if (total == 0)
        {
            return new LegacyMd5BackfillResult { Total = 0, Promoted = 0, Failed = 0 };
        }

        int promoted = 0;
        int failed = 0;
        int processed = 0;
        var failedHashes = new List<string>();
        using var t2Sem = new SemaphoreSlim(T2Concurrency);

        await Parallel.ForEachAsync(
            hashes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = T2Concurrency,
                CancellationToken = cancellationToken
            },
            async (chunkHash, ct) =>
            {
                await t2Sem.WaitAsync(ct);
                try
                {
                    var blobName = $"chunks/{chunkHash}";
                    // Download (verifies envelope CRC + Azure MD5 internally
                    // via VerifyDownloadIntegrity). If this throws, the
                    // chunk is corrupt -- leave its expected MD5 null so
                    // a future integrity check still flags it.
                    var decrypted = await _blobService.DownloadChunkAsync(blobName, ct);

                    // Re-fetch the live ContentHash; this is what the
                    // integrity-check engine will compare against in the
                    // future, so we capture exactly that value.
                    var (exists, _, azureMd5) = await _blobService.GetChunkPropertiesAsync(blobName, ct);
                    if (!exists || azureMd5 == null || azureMd5.Length != 16)
                    {
                        Interlocked.Increment(ref failed);
                        lock (failedHashes) failedHashes.Add(chunkHash);
                        return;
                    }

                    _databaseService.SetChunkExpectedMd5(chunkHash, azureMd5);
                    Interlocked.Increment(ref promoted);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    lock (failedHashes) failedHashes.Add(chunkHash);
                    Log($"BackfillLegacyMd5Async: {chunkHash[..8]}... {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    t2Sem.Release();
                    var done = Interlocked.Increment(ref processed);
                    // Throttle progress to every 10 chunks (or the final one)
                    // so UI updates stay cheap on big corpora.
                    if (done % 10 == 0 || done == total)
                    {
                        progress?.Report(new LegacyMd5BackfillProgress(
                            done, total,
                            Volatile.Read(ref promoted),
                            Volatile.Read(ref failed)));
                    }
                }
            });

        Log($"BackfillLegacyMd5Async: complete -- promoted={promoted}, failed={failed} of {total}");
        return new LegacyMd5BackfillResult
        {
            Total = total,
            Promoted = promoted,
            Failed = failed,
            FailedChunkHashes = failedHashes
        };
    }

    /// <summary>
    /// T1 + T2 + T3 escalation for a single file. Returns the failures
    /// produced; an empty list means the file is clean.
    /// </summary>
    private async Task<List<IntegrityCheckFailure>> CheckOneFileAsync(
        int fileId, int runId,
        IReadOnlyDictionary<int, BackedUpFile> corpus,
        SemaphoreSlim t1Sem, SemaphoreSlim t2Sem,
        CancellationToken cancellationToken)
    {
        var failures = new List<IntegrityCheckFailure>();
        if (!corpus.TryGetValue(fileId, out var fileFromDb))
        {
            failures.Add(new IntegrityCheckFailure
            {
                RunId = runId,
                FileId = fileId,
                LocalPath = $"<unknown file id {fileId}>",
                FailureTier = 1,
                FailureReason = "missing-file-record",
                Detail = "{}"
            });
            return failures;
        }

        // Per-file diag is created lazily so a clean file produces no .diag
        // (option b from the design discussion). It IS created for every
        // failure regardless of tier (you tightened the design here).
        FileOperationDiagnostics? diag = null;
        FileOperationDiagnostics EnsureDiag()
        {
            return diag ??= new FileOperationDiagnostics(
                fileFromDb.LocalPath, "IntegrityCheck", DiagnosticsDirectory);
        }

        try
        {
            // Process chunks sequentially within a single file -- the engine
            // semaphores (t1Sem/t2Sem) bound the network concurrency at the
            // engine level, so adding per-file parallelism here would be
            // accidental complexity for no measurable benefit (see review
            // 3.2). The file-level Parallel.ForEachAsync gives 8-way
            // file parallelism; that is plenty.
            foreach (var chunk in fileFromDb.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CheckOneChunkAsync(fileFromDb, chunk, runId, EnsureDiag, failures, t1Sem, t2Sem, cancellationToken);
            }

            // Always flush the diag if it was created (i.e., any failure).
            if (diag != null)
            {
                var summary = failures.Count == 0
                    ? null
                    : $"{failures.Count} failures: " +
                      string.Join(", ", failures.Select(f => $"{f.FailureReason}@T{f.FailureTier}").Distinct());
                var diagPath = diag.Flush(summary);
                foreach (var f in failures.Where(f => f.DiagFilePath == null))
                {
                    f.DiagFilePath = diagPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (diag != null) diag.Flush("cancelled mid-check");
            throw;
        }
        catch (Exception ex)
        {
            // Engine-level failure on a single file shouldn't abort the run.
            Log($"CheckOneFileAsync: {fileFromDb.LocalPath}: {ex.GetType().Name}: {ex.Message}");
            if (diag != null)
            {
                diag.RecordError("CheckOneFileAsync", ex);
                var diagPath = diag.Flush($"engine error: {ex.Message}");
                failures.Add(new IntegrityCheckFailure
                {
                    RunId = runId,
                    FileId = fileId,
                    LocalPath = fileFromDb.LocalPath,
                    FailureTier = 1,
                    FailureReason = "engine-error",
                    Detail = Detail(("exception", ex.GetType().Name), ("message", ex.Message)),
                    DiagFilePath = diagPath
                });
            }
        }

        return failures;
    }

    /// <summary>
    /// Three-tier check for a single chunk. Records failures into
    /// <paramref name="failures"/> as it escalates. Network calls are
    /// gated by the engine-shared <paramref name="t1Sem"/> (HEAD) and
    /// <paramref name="t2Sem"/> (full download) semaphores.
    /// </summary>
    private async Task CheckOneChunkAsync(
        BackedUpFile file,
        ChunkInfo chunk,
        int runId,
        Func<FileOperationDiagnostics> ensureDiag,
        List<IntegrityCheckFailure> failures,
        SemaphoreSlim t1Sem,
        SemaphoreSlim t2Sem,
        CancellationToken cancellationToken)
    {
        var blobName = $"chunks/{chunk.Hash}";
        // D7 review fix 1.11: defensive bounds check on chunk.Length so
        // a malformed DB row does not later trip ArrayPool.Rent or read
        // a non-sense byte range from the local file. Negative or
        // zero-length chunks cannot describe real data.
        if (chunk.Length <= 0)
        {
            failures.Add(new IntegrityCheckFailure
            {
                RunId = runId,
                FileId = file.Id,
                LocalPath = file.LocalPath,
                FailureTier = 1,
                FailureReason = "invalid-chunk-length",
                ChunkHash = chunk.Hash,
                Detail = Detail(("chunkIndex", chunk.Index), ("length", chunk.Length))
            });
            return;
        }
        var expectedEncryptedLength = chunk.Length + EncryptionService.EncryptionOverhead;

        // -------- T1: HEAD (gated by engine-shared t1Sem) --------
        bool exists;
        long contentLength;
        byte[]? azureMd5;
        await t1Sem.WaitAsync(cancellationToken);
        try
        {
            (exists, contentLength, azureMd5) = await _blobService.GetChunkPropertiesAsync(blobName, cancellationToken);
        }
        finally
        {
            t1Sem.Release();
        }
        if (!exists)
        {
            ensureDiag().RecordChunk("T1", chunk.Index, chunk.Hash, chunk.Length, extra: "missing-blob");
            failures.Add(NewFailure(runId, file, chunk, tier: 1, reason: "missing-blob",
                detail: Detail(("chunkIndex", chunk.Index), ("totalChunks", file.Chunks.Count))));
            return; // no point downloading what isn't there
        }
        if (contentLength != expectedEncryptedLength)
        {
            ensureDiag().RecordChunk("T1", chunk.Index, chunk.Hash, chunk.Length, (int)contentLength,
                extra: $"wrong-size expected={expectedEncryptedLength}");
            failures.Add(NewFailure(runId, file, chunk, tier: 1, reason: "wrong-size",
                detail: Detail(("expectedSize", expectedEncryptedLength), ("actualSize", contentLength))));
            // Escalate to T2 to capture the corruption envelope evidence too;
            // the wrong-size already constitutes a confirmed problem.
        }

        // D6: T1 MD5 comparison (trust-on-first-use). The cheap T1 tier
        // pre-D6 was structural-only: it could not detect same-size
        // envelope corruption. Now we compare the live Azure-side
        // ContentHash against a persisted expected value:
        //   - First time we see a chunk and the persisted MD5 is null,
        //     we capture Azure's current MD5 as the expected. Azure
        //     validates Content-MD5 server-side on PUT so an upload
        //     that completed has a trustworthy initial MD5.
        //   - On subsequent observations, mismatch => T1 md5-mismatch
        //     failure (escalates to T2/T3 for envelope evidence).
        //   - Backends without persistence (LiteDB legacy) silently
        //     skip via _databaseService.GetChunkExpectedMd5 returning
        //     null -- the broader integrity feature is unsupported on
        //     LiteDB anyway.
        var md5Mismatch = false;
        if (azureMd5 != null && azureMd5.Length == 16)
        {
            var persistedMd5 = _databaseService.GetChunkExpectedMd5(chunk.Hash);
            if (persistedMd5 == null)
            {
                // First observation: capture as expected. We deliberately
                // swallow exceptions here -- the integrity check is read-
                // only from the user's perspective and a write failure
                // shouldn't fail the run.
                try { _databaseService.SetChunkExpectedMd5(chunk.Hash, azureMd5); }
                catch (Exception ex) { Log($"SetChunkExpectedMd5({chunk.Hash}): {ex.Message}"); }
            }
            else if (!CryptographicOperations.FixedTimeEquals(persistedMd5, azureMd5))
            {
                md5Mismatch = true;
                ensureDiag().RecordChunk("T1", chunk.Index, chunk.Hash, chunk.Length, (int)contentLength,
                    extra: $"md5-mismatch expected={Convert.ToHexString(persistedMd5)} actual={Convert.ToHexString(azureMd5)}");
                failures.Add(NewFailure(runId, file, chunk, tier: 1, reason: "md5-mismatch",
                    detail: Detail(("chunkIndex", chunk.Index),
                                   ("expectedMd5", Convert.ToHexString(persistedMd5)),
                                   ("actualMd5", Convert.ToHexString(azureMd5)))));
            }
        }

        // -------- T2: full blob --------
        // Escalate when T1 found ANY problem (missing-size mismatch OR D6
        // md5-mismatch). The cheap path stays cheap for clean chunks; a
        // chunk with bad bytes pays the T2 price for the envelope-level
        // evidence (and T3 byte-compare against local).
        var t1Failed = contentLength != expectedEncryptedLength || md5Mismatch;
        if (!t1Failed) return;

        byte[]? decrypted = null;
        Metrics?.RecordDecision("integrity-check-tier-escalation", new Dictionary<string, object?>
        {
            ["file"] = file.LocalPath,
            ["chunk"] = chunk.Hash,
            ["from"] = "T1",
            ["to"] = "T2"
        });
        // T2 (and T3 byte-compare which depends on T2's decrypted bytes)
        // are gated by t2Sem to bound download bandwidth on a heavily-
        // corrupted run. Held only over the actual network download;
        // T3's local-file re-read happens after release.
        await t2Sem.WaitAsync(cancellationToken);
        try
        {
            try
            {
                // DownloadChunkAsync runs VerifyDownloadIntegrity (X1b/X2) and
                // emits the [CRC FAIL] diag itself if the envelope CRC fails.
                decrypted = await _blobService.DownloadChunkAsync(blobName, cancellationToken);
            }
            catch (DataIntegrityException ex)
            {
                ensureDiag().RecordChunk("T2", chunk.Index, chunk.Hash, chunk.Length, (int)contentLength,
                    crcValid: false, extra: $"crc-or-md5-fail: {ex.Message}");
                failures.Add(NewFailure(runId, file, chunk, tier: 2, reason: "crc-mismatch",
                    detail: Detail(("chunkIndex", chunk.Index), ("message", ex.Message))));
                return;
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                ensureDiag().RecordChunk("T2", chunk.Index, chunk.Hash, chunk.Length, (int)contentLength,
                    crcValid: false, extra: $"decrypt-failed: {ex.Message}");
                failures.Add(NewFailure(runId, file, chunk, tier: 2, reason: "decrypt-failed",
                    detail: Detail(("chunkIndex", chunk.Index), ("message", ex.Message))));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ensureDiag().RecordChunk("T2", chunk.Index, chunk.Hash, chunk.Length, (int)contentLength,
                    extra: $"download-error: {ex.GetType().Name}");
                failures.Add(NewFailure(runId, file, chunk, tier: 2, reason: "download-error",
                    detail: Detail(("chunkIndex", chunk.Index), ("exception", ex.GetType().Name))));
                return;
            }
        }
        finally
        {
            t2Sem.Release();
        }

        // -------- T3: byte compare against local file --------
        Metrics?.RecordDecision("integrity-check-tier-escalation", new Dictionary<string, object?>
        {
            ["file"] = file.LocalPath,
            ["chunk"] = chunk.Hash,
            ["from"] = "T2",
            ["to"] = "T3"
        });
        if (!File.Exists(file.LocalPath))
        {
            ensureDiag().RecordChunk("T3", chunk.Index, chunk.Hash, chunk.Length,
                extra: "local-file-missing");
            failures.Add(NewFailure(runId, file, chunk, tier: 3, reason: "local-file-missing",
                detail: Detail(("chunkIndex", chunk.Index), ("localPath", file.LocalPath))));
            return;
        }

        // Re-read just this chunk's bytes from disk -- bounded by chunk.Length.
        var rented = ArrayPool<byte>.Shared.Rent(chunk.Length);
        try
        {
            int read;
            using (var fs = new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Position = chunk.Offset;
                read = await fs.ReadAsync(rented.AsMemory(0, chunk.Length), cancellationToken);
            }
            if (read != chunk.Length)
            {
                ensureDiag().RecordChunk("T3", chunk.Index, chunk.Hash, chunk.Length,
                    extra: $"local-short-read: got={read} expected={chunk.Length}");
                failures.Add(NewFailure(runId, file, chunk, tier: 3, reason: "local-short-read",
                    detail: Detail(("chunkIndex", chunk.Index), ("got", read), ("expected", chunk.Length))));
                return;
            }

            var localSpan = rented.AsSpan(0, chunk.Length);
            var match = decrypted.Length == chunk.Length &&
                        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(localSpan, decrypted);
            if (!match)
            {
                ensureDiag().RecordChunk("T3", chunk.Index, chunk.Hash, chunk.Length, decrypted.Length,
                    extra: "byte-differ");
                failures.Add(NewFailure(runId, file, chunk, tier: 3, reason: "byte-differ",
                    detail: Detail(("chunkIndex", chunk.Index), ("localLen", chunk.Length), ("remoteLen", decrypted.Length))));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static IntegrityCheckFailure NewFailure(
        int runId, BackedUpFile file, ChunkInfo chunk, int tier, string reason, string detail)
    {
        return new IntegrityCheckFailure
        {
            RunId = runId,
            FileId = file.Id,
            LocalPath = file.LocalPath,
            FailureTier = tier,
            FailureReason = reason,
            ChunkHash = chunk.Hash,
            Detail = detail
        };
    }

    /// <summary>
    /// D7 review fix 1.9 (refined by I4): build a JSON detail object
    /// via System.Text.Json so unusual filenames / exception messages
    /// with backslashes, quotes, control characters, or non-ASCII
    /// don't produce invalid JSON. Uses <see cref="JsonObject"/>
    /// rather than <c>Dictionary&lt;string,object?&gt;</c> so the
    /// enumeration order is contractually insertion-order
    /// (Dictionary's order is implementation-defined, even if it
    /// happens to be insertion-order in modern .NET).
    /// </summary>
    private static string Detail(params (string key, object? value)[] pairs)
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in pairs)
        {
            node[k] = v switch
            {
                null => null,
                string s => s,
                int i => i,
                long l => l,
                bool b => b,
                double d => d,
                _ => v.ToString()
            };
        }
        return node.ToJsonString();
    }
}
