using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using AzureBackup.Core.Services;

namespace AzureBackup.Benchmarks;

/// <summary>
/// W6 follow-up: isolates the AIMD start-low ramp cost (B84) from the rest of
/// the W6 pipeline changes (B81 CDC speedup, B83 two-lane split) on the two
/// workloads that regressed in the post-W6 <see cref="BackupThroughputBenchmark"/>
/// run -- <c>realistic-large-50</c> and <c>realistic-large-200</c>, which are
/// ~90% files in the 30-100 MB range and therefore route almost entirely
/// through the AIMD-governed large-file lane.
///
/// <para>
/// <b>The question.</b> The post-W6 throughput run showed those two workloads
/// roughly 50% / 117% SLOWER than the B27 baseline, while every workload that
/// stays out of the large lane (uniform-1MB, large-skew) got 9-15% FASTER. The
/// hypothesis: the regression is NOT a real pipeline change but the AIMD
/// scheduler starting the large lane at <c>min(4, ceiling)</c> and ramping
/// additively against a 2-second sample window -- which, on the network-free
/// <see cref="InMemoryBlobService"/> (zero latency, no bandwidth knee), is pure
/// startup overhead with none of AIMD's real-link benefit.
/// </para>
///
/// <para>
/// <b>The experiment.</b> Each workload runs twice in the same BDN invocation
/// (same host, back-to-back, eliminating cross-run host variance): once with
/// the production AIMD start-low behaviour (<c>StartAtCeiling=false</c>) and
/// once with the new <see cref="BackupOrchestrator.AimdStartAtCeilingOverride"/>
/// seam that starts the lane at the budget-derived ceiling
/// (<c>StartAtCeiling=true</c>, i.e. no ramp). If the hypothesis holds, the
/// <c>StartAtCeiling=true</c> arm should erase the regression -- and, because
/// it runs on the post-W6 codebase, should additionally show the B81 CDC win
/// (faster than the B27 baseline) on these workloads.
/// </para>
///
/// <para>
/// <b>What it does NOT measure.</b> Real Azure egress. AIMD exists to fit a
/// real link's bandwidth; a zero-latency fake cannot show that benefit, so
/// this benchmark only quantifies the ramp's COST in the CPU-bound case. The
/// production startup-policy decision must also weigh a latency-injected run
/// (see the W6 status note in AGENT_CONTEXT) and a real-account transfer.
/// </para>
///
/// <para>
/// Same hardware/config contract as <see cref="BackupThroughputBenchmark"/>
/// (<c>warmupCount: 1, iterationCount: 2, invocationCount: 1</c>) so the
/// per-arm Means are directly comparable to that benchmark's rows and to the
/// B27 baseline table.
/// </para>
///
/// <para>
/// <b>Result (captured 2026-06-11, AMD EPYC 7763 @ 2.44 GHz, 16 logical / 8
/// physical cores in Hyper-V, .NET 10.0.9, same warmup/iteration config):</b>
/// <code>
/// // | Workload            | StartAtCeiling |   Mean   | Allocated |
/// // |-------------------- |--------------- |--------- |---------- |
/// // | realistic-large-50  | False (ramp)   |  4.06 s  |  2.75 GB  |
/// // | realistic-large-50  | True  (ceiling)|  2.03 s  |  2.98 GB  |
/// // | realistic-large-200 | False (ramp)   | 26.71 s  | 11.14 GB  |
/// // | realistic-large-200 | True  (ceiling)| 17.93 s  | 11.47 GB  |
/// </code>
/// <b>Conclusion.</b> Removing the AIMD start-low ramp (the clean WITHIN-RUN
/// comparison, same host, back-to-back) makes the large-file lane
/// <b>2.0x faster on realistic-large-50</b> (4.06 -> 2.03 s) and
/// <b>1.49x faster on realistic-large-200</b> (26.71 -> 17.93 s). The
/// hypothesis is confirmed: the post-W6 throughput regression on these
/// workloads is the start-low ramp, not a real pipeline change. Cross-checked
/// against the B27 baseline (12.35 s / 2.72 s -- a DIFFERENT VM instance and
/// .NET patch, so secondary), the start-ceiling realistic-large-50 (2.03 s)
/// lands BELOW baseline, exposing the B81 CDC win; realistic-large-200
/// (17.93 s) still sits above its 12.35 s baseline, a residual that a same-VM
/// pre-W6 control would be needed to attribute cleanly (two-lane / AIMD
/// per-chunk bookkeeping overhead at high file count, or cross-VM baseline
/// noise). The slightly higher Allocated at StartAtCeiling=true is the
/// expected cost of more files in flight concurrently; both stay within the
/// (unlimited, for this small workload) budget. This is a CPU-bound,
/// network-free measurement: it quantifies the ramp's COST but not AIMD's
/// real-link BENEFIT, which requires a latency-injected run.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class AimdStartupIsolationBenchmark : BackupBenchmarkBase
{
    /// <summary>The two large-file-dominated workloads that regressed post-W6.</summary>
    [Params("realistic-large-50", "realistic-large-200")]
    public override string Workload { get; set; } = "realistic-large-50";

    /// <summary>
    /// The isolation variable. <c>false</c> = production AIMD start-low ramp;
    /// <c>true</c> = AIMD large lane starts at the budget-derived ceiling (no
    /// ramp), via <see cref="BackupOrchestrator.AimdStartAtCeilingOverride"/>.
    /// </summary>
    [Params(false, true)]
    public bool StartAtCeiling { get; set; }

    protected override void ConfigureOrchestrator(BackupOrchestrator orchestrator)
        => orchestrator.AimdStartAtCeilingOverride = StartAtCeiling;

    [Benchmark(Description = "End-to-end backup, AIMD start-low vs start-at-ceiling")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet("Backup");
        }
    }
}
