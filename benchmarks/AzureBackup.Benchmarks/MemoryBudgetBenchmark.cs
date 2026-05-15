using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B27: first-ever measurement of the user-visible MemoryLimitMB
/// slider. Every pre-B27 benchmark ran with
/// <c>memoryBudget=unlimited</c> because the user's 2026-04-23
/// production log showed that setting in use. The recommendation
/// going forward is to run with <c>MemoryLimitMB=16384</c>, so this
/// sweep measures whether that value actually binds and what
/// throughput cost it imposes relative to both unlimited and to
/// lower limits a constrained user might pick.
///
/// <para>
/// <b>Sweep.</b> <c>MemoryLimitParam</c> maps to the settings the UI
/// actually exposes on the slider (512, 1024, 2048, 4096, 8192,
/// 16384, 32768 MB). This benchmark picks a subset:
/// <list type="bullet">
///   <item><c>0</c> (sentinel for unlimited / slider disabled) --
///     the pre-B27 baseline behaviour.</item>
///   <item><c>16384</c> (16 GB) -- the recommended production
///     setting.</item>
///   <item><c>8192</c> (8 GB) -- the "safe for a 16 GB machine"
///     setting most users on commodity hardware would pick.</item>
///   <item><c>4096</c> (4 GB) -- the "tightly constrained" setting
///     for users on older hardware; should expose whether
///     MemoryBudget degrades throughput gracefully or has a hard
///     cliff.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Workload choice.</b> Only <c>media-library-500</c>. It is the
/// workload where MemoryBudget would ever bind: large files produce
/// many simultaneous 64 MB chunk buffers, the theoretical in-flight
/// ceiling is multi-GB, and the production peak WS observation that
/// motivated the recommendation in the first place came from a
/// media-heavy workload. The other two big-scale workloads would
/// either not stress the budget enough
/// (<c>production-scale-3000</c>) or would be dominated by the
/// single-file latency of the 20 GB outlier
/// (<c>huge-outlier-mixed</c>), making the budget-throughput curve
/// unreadable.
/// </para>
///
/// <para>
/// <b>What this benchmark resolves.</b> Produces a defensible
/// default value for the UI's MemoryLimitMB slider and a defensible
/// recommendation for what to do with it. If 4 GB and 16 GB have
/// the same throughput, the slider is cosmetic and we should ship
/// a sensible low default (say 4 GB) with <c>MemoryLimitEnabled</c>
/// ON by default. If 4 GB throttles heavily but 16 GB matches
/// unlimited, 16 GB is the right default for machines with at least
/// 32 GB RAM.
/// </para>
///
/// <para>
/// <b>Results (captured 2026-04-25, hardware: AMD EPYC 7763 @
/// 2.44 GHz, 16 logical / 8 physical cores in Hyper-V, .NET 10.0.6,
/// SQLite backend, retainPayloads=false, MaxParallelFileBackups=8
/// (pre-B27 default), warmupCount=1 iterationCount=2 invocationCount=1):</b>
/// <code>
/// // | MemoryLimitParam | Mean    | Allocated  | vs unlimited |
/// // |----------------- |-------: |---------:  |------------: |
/// // | 0 (unlimited)    | 4.937 m | 258.54 GB  |        +0.0% |
/// // | 16384            | 4.721 m | 257.40 GB  |        -4.4% |
/// // | 8192             | 4.744 m | 257.39 GB  |        -3.9% |
/// // | 4096             | 4.726 m | 258.27 GB  |        -4.3% |
/// </code>
/// <b>Conclusion</b>: MemoryBudget does not throttle throughput on
/// <c>media-library-500</c> at any value tested -- all four
/// configurations are within 4.5% of each other (well inside the
/// noise band given N=2). If anything the constrained values run
/// slightly faster than unlimited, plausibly because the GC has
/// tighter targets and is more efficient. This validated turning
/// <c>MemoryLimitEnabled</c> ON by default in B27.
/// <para>
/// <b>Choosing the default value</b>: the sweep ran with
/// <c>MaxParallelFileBackups=8</c> (the pre-B27 default), at which
/// the worst-case in-flight chunk-buffer ceiling is
/// 8 files x 6 chunks x 64 MB = 3 GB. With B27's bump to 16-way the
/// ceiling becomes 6 GB, which means <c>MemoryLimitMB=4096</c> would
/// force stalls under the new default. <c>8192</c> is the smallest
/// stepped value that fits the 16-way ceiling without throttling, so
/// it is the value B27 ships as the new default.
/// </para>
/// </para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(MemoryFidelityConfig))]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class MemoryBudgetBenchmark : BackupBenchmarkBase
{
    /// <summary>
    /// W5 Phase 1 (B64): the media-library-500 row stays as the
    /// pre-W5 anchor so the result table in this xmldoc remains
    /// directly comparable. The three adversarial-* rows are the
    /// new W5 workloads designed to actually bind the budget; see
    /// <see cref="BackupBenchmarkBase.AdversarialWorkloads"/> for
    /// what each one stresses.
    /// </summary>
    [Params(
        "media-library-500",
        "adversarial-large-chunks",
        "adversarial-pool-churn",
        "adversarial-mixed")]
    public override string Workload { get; set; } = "media-library-500";

    /// <summary>
    /// MemoryLimitMB value for this iteration. Zero is the sentinel
    /// for the pre-B27 behaviour (unlimited budget / slider disabled).
    /// All other values are applied verbatim as <c>MemoryLimitMB</c>
    /// with <c>MemoryLimitEnabled=true</c>.
    /// </summary>
    [Params(0, 16384, 8192, 4096, 2048)]
    public int MemoryLimitParam { get; set; }

    protected override int? MemoryLimitMBOverride =>
        MemoryLimitParam == 0 ? null : MemoryLimitParam;

    // B27: discard mode. media-library-500 cannot fit its encrypted
    // ciphertext in RAM, and the whole point of this sweep is to
    // measure the orchestrator's MemoryBudget behaviour rather than
    // the in-memory destination's; see InMemoryBlobService summary.
    protected override bool RetainBlobPayloads => false;

    /// <summary>
    /// W5 Phase 1 (B64): opt into the new BDN fidelity columns so
    /// every row in this benchmark's summary reports
    /// <c>PeakWS_MB</c>, <c>MaxUnacc_MB</c>, <c>PeakBudget_MB</c>,
    /// <c>Overshoot</c>, <c>Stalls/GB</c>, and <c>Oversized</c>
    /// alongside <c>Mean</c> and <c>Allocated</c>.
    /// </summary>
    protected override bool EnableMemoryFidelityTracking => true;

    [Benchmark(Description = "Backup at parametric MemoryBudget with W5 fidelity tracking")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        StartFidelityIteration(MemoryLimitParam);
        var totalBytes = 0L;
        try
        {
            // Capture the workload total before BackupFilesAsync so
            // EndFidelityIteration can compute Stalls/GB even if the
            // operation throws partway through.
            foreach (var p in FilePaths) totalBytes += new FileInfo(p).Length;
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet($"MemoryLimitMB={MemoryLimitParam}");
            EndFidelityIteration(totalBytes);
        }
    }
}
