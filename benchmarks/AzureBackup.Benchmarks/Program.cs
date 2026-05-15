using BenchmarkDotNet.Running;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Entry point for local-developer benchmarks. Not run in CI.
///
/// Usage:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks
///
/// Or to filter to a single benchmark:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks -- --filter *Phase1*
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // W5 Phase 1 (B64): fail fast if the BackupMemoryReporter
        // emit format has drifted from MemoryFidelityCollector's
        // regexes. Without this check a future format change would
        // cause every fidelity column to render as "-" silently;
        // the user only finds out hours into a Phase 2 baseline run.
        VerifyMemoryFidelityParserContract();

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static void VerifyMemoryFidelityParserContract()
    {
        // Synthetic line shaped exactly like
        // BackupMemoryReporter.EmitSample emits with a wired
        // LargeChunkBufferPool. The numbers here are arbitrary but
        // distinct so a regex misalignment would surface as a
        // wrong-value mismatch rather than a silent zero.
        const string sample =
            "[mem] backup t+12s | budget used=4096 MB / 8192 MB (50.0%, peak=6144 MB) | " +
            "stalls +0 (total 7) | oversized +0 (total 3) | " +
            "gcHeap=512 MB | gcLoad=8192 MB | " +
            "workingSet=10240 MB | privateMem=10240 MB | " +
            "unaccounted=2048 MB | gcCollections=[3,1,0] | " +
            "lohPool=512 MB cached (peak=768 MB, dropped=0, hit=80%)";

        MemoryFidelityCollector.Instance.StartIteration("__contract_check__", "__sample__", 8192);
        MemoryFidelityCollector.Instance.RecordSampleLine(sample);
        MemoryFidelityCollector.Instance.EndIteration(bytesProcessed: 0);

        var result = MemoryFidelityCollector.Instance.Results
            .FirstOrDefault(r => r.BenchmarkName == "__contract_check__");

        if (result is null) throw new InvalidOperationException(
            "MemoryFidelityCollector contract check failed: no result recorded.");

        const long MB = 1024L * 1024L;
        if (result.PeakWorkingSetBytes != 10240 * MB) throw Drift("workingSet", 10240, result.PeakWorkingSetBytes / MB);
        if (result.MaxUnaccountedBytes != 2048 * MB) throw Drift("unaccounted", 2048, result.MaxUnaccountedBytes / MB);
        if (result.PeakBudgetUsedBytes != 6144 * MB) throw Drift("peak budget", 6144, result.PeakBudgetUsedBytes / MB);
        if (result.StallCount != 7) throw Drift("stalls", 7, result.StallCount);
        if (result.OversizedAdmissions != 3) throw Drift("oversized", 3, result.OversizedAdmissions);

        static InvalidOperationException Drift(string field, long expected, long actual) =>
            new($"MemoryFidelityCollector parser drift on '{field}': expected {expected}, got {actual}. " +
                "BackupMemoryReporter.EmitSample format has changed; update MemoryFidelityCollector regexes.");
    }
}
