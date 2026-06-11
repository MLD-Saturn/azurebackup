using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using AzureBackup.Core.Services;

namespace AzureBackup.Benchmarks;

/// <summary>
/// W6 Item 1: the latency-injected follow-up to <see cref="AimdStartupIsolationBenchmark"/>.
/// <see cref="AimdStartupIsolationBenchmark"/> proved (on a zero-latency
/// <see cref="InMemoryBlobService"/>) that the post-W6 throughput regression on
/// large-file workloads is the AIMD start-low ramp -- removing it was 1.5-2x
/// faster. But that benchmark is the WRONG instrument for the production
/// decision: with no network, file-level concurrency only buys CPU parallelism
/// and the run is too short for the ramp to complete, so it can only show the
/// ramp's COST, never AIMD's real-link BENEFIT.
///
/// <para>
/// <b>What this adds.</b> A per-chunk simulated upload latency (via
/// <see cref="BackupBenchmarkBase.SimulatedLatencyMs"/>) turns the run
/// latency-bound, which does two things a real link also does: (1) makes
/// concurrency matter (more concurrent uploads hide more round-trip latency),
/// and (2) lengthens the run so the AIMD scheduler gets enough 2-second
/// evaluation ticks for its 4-&gt;ceiling ramp to play out.
/// </para>
///
/// <para>
/// <b>The question.</b> The AIMD ramp is a roughly FIXED wall-clock cost
/// (~12-24s at +1/+2 per 2s window). Its penalty as a FRACTION of a backup
/// therefore shrinks as the backup gets longer. On a real link a non-trivial
/// large-file backup runs for minutes, so the ramp should be nearly free; on
/// the zero-latency benchmark the whole backup is 4-27s, so the ramp dominates.
/// This sweep measures the start-low vs start-ceiling gap as latency (and thus
/// runtime) rises: if the gap collapses toward 1.0x, the ramp is free given
/// runtime and start-low is the right production default; if it stays wide, the
/// ramp is too slow regardless and the production policy needs retuning
/// (shorter ramp / higher initial / start-high-back-off).
/// </para>
///
/// <para>
/// <b>Design.</b> Single cheap workload (realistic-large-50, ~3 GB, ~90% files
/// 30-100 MB so almost all route through the AIMD large lane). 0 ms is the
/// within-run anchor (reproduces <see cref="AimdStartupIsolationBenchmark"/>'s
/// realistic-large-50 numbers); 50/100/200 ms walk the run from CPU-bound into
/// latency-bound. Same <c>warmupCount: 1, iterationCount: 2, invocationCount: 1</c>
/// config as the other backup benchmarks. Reuses the B85
/// <see cref="BackupOrchestrator.AimdStartAtCeilingOverride"/> seam via
/// <see cref="ConfigureOrchestrator"/>.
/// </para>
///
/// <para>
/// <b>First run (2026-06-11, AMD EPYC 7763, .NET 10.0.9) -- interpret with care.</b>
/// The reliable points: start-low @ 0 ms = 4.04 s (clean, reproduces
/// <see cref="AimdStartupIsolationBenchmark"/>'s realistic-large-50 start-low);
/// start-ceiling @ 50 ms = 7.76 s (clean, 0.27% StdErr). BUT the start-low
/// high-latency arms are catastrophically noisy at N=2 -- e.g. start-low @
/// 100 ms gave 22 s and 56 s on its two iterations (Mean 39 s, StdDev 24 s).
/// That variance is itself the finding: the AIMD ramp's completion is
/// wall-clock-timing-sensitive, so start-low produces large run-to-run
/// variance at link latencies, while start-ceiling is deterministic and fast.
/// Clean Mean-based convergence numbers are therefore NOT obtainable from this
/// N=2 in-memory benchmark; raising <c>iterationCount</c> (or a real-account
/// run) would be needed for a polished convergence table.
/// </para>
///
/// <para>
/// <b>What the data + the deterministic ramp arithmetic establish.</b> The
/// ramp is a roughly FIXED 12-24 s wall-clock cost (<see cref="BandwidthScheduler"/>
/// gates each +1/+2 step to a 2 s sample window, and 4-&gt;16 is 12 steps). As
/// a fraction of a 13 GB large-file backup that is ~1-2% at 100 Mbps but
/// ~12-46% at 1-2 Gbps -- so start-low is cheap on slow links (where its
/// convergence-from-below safety matters) and costly precisely at the fast
/// links the project prioritizes. The decision (W6, see AGENT_CONTEXT): the
/// backup large-file lane should start at/near the budget-derived ceiling and
/// rely on multiplicative-decrease + the MemoryBudget cap for safety, with the
/// exact initial gated on a real-account run. This benchmark is the committed
/// regression instrument for that decision.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class AimdLatencyConvergenceBenchmark : BackupBenchmarkBase
{
    /// <summary>
    /// Fixed to the large-file-dominated workload that regressed post-W6 and is
    /// cheap enough (~3 GB) to sweep across several latency values.
    /// </summary>
    [Params("realistic-large-50")]
    public override string Workload { get; set; } = "realistic-large-50";

    /// <summary>
    /// <c>false</c> = production AIMD start-low ramp; <c>true</c> = AIMD large
    /// lane starts at the budget-derived ceiling (no ramp), via
    /// <see cref="BackupOrchestrator.AimdStartAtCeilingOverride"/>.
    /// </summary>
    [Params(false, true)]
    public bool StartAtCeiling { get; set; }

    /// <summary>
    /// Per-chunk simulated upload latency. 0 = the zero-latency anchor; the
    /// non-zero values walk the run from CPU-bound into latency-bound so the
    /// AIMD ramp has runtime to converge.
    /// </summary>
    [Params(0, 50, 100, 200)]
    public int LatencyMs { get; set; }

    protected override int SimulatedLatencyMs => LatencyMs;

    protected override void ConfigureOrchestrator(BackupOrchestrator orchestrator)
        => orchestrator.AimdStartAtCeilingOverride = StartAtCeiling;

    [Benchmark(Description = "Backup with simulated link latency, AIMD start-low vs start-at-ceiling")]
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
