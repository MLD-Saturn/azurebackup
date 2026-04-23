namespace AzureBackup.Core.Models;

/// <summary>
/// User-selected scope and behaviour for a single integrity-check run.
/// Built up by the UI's scope panel (file checkbox tree + time/history
/// dropdown) and consumed by <c>IntegrityCheckService.RunAsync</c>.
/// </summary>
/// <remarks>
/// The set of files to check is supplied directly via <see cref="FileIds"/>
/// rather than being recomputed from <see cref="ScopePreset"/> + filters --
/// the UI does the bidirectional sync between the dropdown and the tree
/// before submitting, and we want the engine to be deterministic about
/// which files it will touch (so a re-check or replay is exact).
/// <see cref="ScopePreset"/> is recorded only for human-readable
/// scope-summary text on the run row.
/// </remarks>
public sealed class IntegrityCheckOptions
{
    /// <summary>
    /// Files to check, identified by <c>BackedUpFile.Id</c>. Must be
    /// non-empty. The engine fetches each file's metadata + chunk list
    /// from the local DB and checks every chunk in those files.
    /// </summary>
    public required IReadOnlyList<int> FileIds { get; init; }

    /// <summary>
    /// Human-readable scope label preserved on the run row's
    /// <c>scope_summary</c> column. Examples:
    /// <c>"This session (47 files)"</c>,
    /// <c>"Last 24h (1043 files)"</c>,
    /// <c>"Re-check failures from run #7 (3 files)"</c>,
    /// <c>"Custom selection (12 files)"</c>.
    /// </summary>
    public required string ScopeSummary { get; init; }

    /// <summary>
    /// True when this run is a re-check of a previous run's failures.
    /// Recorded so the History expander can show lineage
    /// (<see cref="ParentRunId"/> points at the original run).
    /// </summary>
    public bool IsReCheckOfFailures { get; init; }

    /// <summary>
    /// When <see cref="IsReCheckOfFailures"/> is true, the id of the
    /// originating run. Null otherwise.
    /// </summary>
    public int? ParentRunId { get; init; }

    /// <summary>
    /// When true and the run produces any failures of any tier, the
    /// engine fires <c>DiagnosticBundleExporter.Export</c> and stores
    /// the produced ZIP path on the run row's <c>diag_bundle_path</c>
    /// column. The tester then has a single attachment to share.
    /// </summary>
    public bool AutoExportBundleOnFailure { get; init; } = true;
}

/// <summary>
/// Persisted summary of a single integrity-check run. One row per
/// <c>IntegrityCheckService.RunAsync</c> invocation. Retention: keep the
/// most recent 30 rows, prune older.
/// </summary>
/// <remarks>
/// Counters use the tier semantics established in the design discussion:
/// <list type="bullet">
///   <item>T1 = structural HEAD-only check (existence, length)</item>
///   <item>T2 = full-blob download with envelope CRC + AES-GCM tag check</item>
///   <item>T3 = byte-for-byte comparison vs. the local file's chunk segment</item>
/// </list>
/// <c>FilesPassed</c> means the file's chunks all passed T1 (no escalation
/// needed). <c>FilesFailed*</c> are mutually exclusive: a file is
/// classified by the deepest tier it tripped.
/// </remarks>
public sealed class IntegrityCheckRun
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>
    /// Correlates with <c>CrashSafeLogger.SessionId</c> so a triager can
    /// grep <c>azurebackup-*.log</c> for the same minute. Empty Guid when
    /// no logger session was active (test runs).
    /// </summary>
    public Guid SessionId { get; set; }

    public string ScopeSummary { get; set; } = string.Empty;

    public int FilesChecked { get; set; }
    public int FilesPassed { get; set; }
    public int FilesFailedT1 { get; set; }
    public int FilesFailedT2 { get; set; }
    public int FilesFailedT3 { get; set; }
    public int FilesWarning { get; set; }

    /// <summary>
    /// True when the run was cancelled before processing every file in
    /// scope. Partial-state contract for cancelled runs:
    /// <list type="bullet">
    ///   <item><c>FilesChecked</c>, <c>FilesPassed</c>, <c>FilesFailedT1</c>,
    ///         <c>FilesFailedT2</c>, <c>FilesFailedT3</c>, <c>FilesWarning</c>
    ///         reflect the work completed BEFORE cancellation.</item>
    ///   <item>The integrity_check_failures table contains every failure
    ///         that was discovered before cancellation.</item>
    ///   <item><c>FinishedUtc</c> is the cancellation timestamp, not the
    ///         scope's natural completion time.</item>
    ///   <item>Files that were in scope but never processed have NO row
    ///         and are NOT counted in any of the Files* totals.</item>
    /// </list>
    /// A re-check of a cancelled run will re-process files that were
    /// already classified before cancel.
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// When <see cref="IntegrityCheckOptions.IsReCheckOfFailures"/> is true,
    /// the id of the originating run; null otherwise.
    /// </summary>
    public int? ParentRunId { get; set; }

    /// <summary>
    /// Path to a diagnostic bundle ZIP if one was auto-generated, else null.
    /// </summary>
    public string? DiagBundlePath { get; set; }
}

/// <summary>
/// One row per file-level failure in the most recent run. When a new run
/// starts the engine deletes every row in this table so the panel only
/// ever shows results for the latest run -- historical detail lives in
/// the <c>.diag</c> files which are independently retained.
/// </summary>
public sealed class IntegrityCheckFailure
{
    public int Id { get; set; }
    public int RunId { get; set; }

    /// <summary><c>BackedUpFile.Id</c> (foreign key, not enforced).</summary>
    public int FileId { get; set; }

    public string LocalPath { get; set; } = string.Empty;

    /// <summary>1, 2, or 3 -- the deepest tier this file's check tripped.</summary>
    public int FailureTier { get; set; }

    /// <summary>
    /// Short categorical label for grouping in the UI. Stable values:
    /// <list type="bullet">
    ///   <item><c>"missing-blob"</c> -- T1: blob does not exist in Azure</item>
    ///   <item><c>"wrong-size"</c> -- T1: ContentLength + envelope-overhead != expected</item>
    ///   <item><c>"size-disagreement"</c> -- T1 warning: local DB sources disagree</item>
    ///   <item><c>"crc-mismatch"</c> -- T2: envelope CRC failed</item>
    ///   <item><c>"md5-mismatch"</c> -- T2: blob MD5 != stored ContentHash</item>
    ///   <item><c>"decrypt-failed"</c> -- T2: AES-GCM tag rejected ciphertext</item>
    ///   <item><c>"byte-differ"</c> -- T3: decrypted data != local file segment</item>
    /// </list>
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Specific chunk hash that failed (for chunk-level failures), or null
    /// for whole-file-scope failures (e.g., file has no chunks recorded).
    /// </summary>
    public string? ChunkHash { get; set; }

    /// <summary>
    /// Compact JSON-formatted detail bag with the failure-specific fields.
    /// Examples: <c>{"expectedSize":4194341,"actualSize":4194304}</c>,
    /// <c>{"chunkIndex":4,"totalChunks":12}</c>.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Path to the per-file <c>.diag</c> file that contains the full
    /// chunk-by-chunk trace of this failure. The UI's
    /// "Open .diag" action targets this path.
    /// </summary>
    public string? DiagFilePath { get; set; }
}

/// <summary>
/// In-memory aggregate returned by <c>IntegrityCheckService.RunAsync</c>.
/// Contains the persisted run row plus the per-file failure detail so a
/// caller can inspect results without re-querying the DB.
/// </summary>
public sealed class IntegrityCheckResult
{
    public required IntegrityCheckRun Run { get; init; }
    public required IReadOnlyList<IntegrityCheckFailure> Failures { get; init; }
}

/// <summary>
/// Progress updates emitted by <c>IntegrityCheckService.RunAsync</c>.
/// Counts are the running totals at each callback so the UI can render a
/// per-tier dashboard in real time.
/// </summary>
public readonly record struct IntegrityCheckProgress(
    int FilesProcessed,
    int FilesTotal,
    string CurrentFile,
    int T1FailCount,
    int T2FailCount,
    int T3FailCount);

/// <summary>
/// D10: result of a one-shot legacy-chunk MD5 backfill scan. The
/// "Promoted" count is the number of chunks whose null
/// <c>expected_encrypted_md5</c> column is now populated; "Failed"
/// is the number of chunks whose download or envelope verification
/// failed (those keep their null MD5 so a future scan can retry).
/// </summary>
public sealed class LegacyMd5BackfillResult
{
    public long Total { get; init; }
    public int Promoted { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> FailedChunkHashes { get; init; } = [];
}

/// <summary>
/// D10: per-chunk progress tick from the backfill scan. Throttled to
/// every 10 chunks (or the final one) so UI updates stay cheap on
/// multi-thousand-chunk corpora.
/// </summary>
public readonly record struct LegacyMd5BackfillProgress(
    int Processed,
    long Total,
    int Promoted,
    int Failed);
