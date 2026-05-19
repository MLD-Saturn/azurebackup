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

        // B66: clean the compile-time-derived samples directory of
        // stale per-PID files from prior runs. This is best-effort
        // and host-only: if Program.Main is bypassed by Visual Studio
        // or dotnet test, prior records may show up in the current
        // summary, but the column provider's row identity match
        // (benchmark + workload + memoryLimitMB) makes that benign --
        // either the keys collide and aggregation absorbs them, or
        // they don't and they're ignored. The compile-time path
        // means we no longer need to publish an env var or rely on
        // child-process inheritance, which is what killed B65.
        CleanFidelitySamplesDirectory();

        // B66: round-trip a synthetic record through the persistence
        // path so a regression on file IO, JSON shape, or directory
        // resolution fails fast instead of silently regressing every
        // fidelity cell to "-" (the B65 lesson). This now executes
        // against the same compile-time-derived path BDN children
        // will use, so a green self-check actually proves the
        // production load path works.
        VerifyMemoryFidelityPersistenceRoundTrip();

        Console.WriteLine(
            $"[B66] Memory-fidelity samples directory: " +
            $"{MemoryFidelityCollector.SamplesDirectory}");

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static void CleanFidelitySamplesDirectory()
    {
        try
        {
            var dir = MemoryFidelityCollector.SamplesDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "samples-pid*.jsonl"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
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

    private static void VerifyMemoryFidelityPersistenceRoundTrip()
    {
        // B66: round-trip a synthetic record through the same
        // compile-time-derived path BDN children will use. The
        // bench-marker tag in BenchmarkName isolates the synthetic
        // row from any real benchmark rows that also live in
        // samples-pid*.jsonl after CleanFidelitySamplesDirectory was
        // skipped (e.g. when Main is bypassed). On success this
        // proves: (1) the directory is writable from this process;
        // (2) JsonSerializer round-trips the record cleanly;
        // (3) LoadPersistedResults finds and parses the line back.
        const string marker = "__persist_check_b66__";

        MemoryFidelityCollector.Instance.StartIteration(marker, "__sample__", 4096);
        MemoryFidelityCollector.Instance.RecordSampleLine(
            "[mem] backup t+1s | budget used=1024 MB / 4096 MB (25.0%, peak=1024 MB) | " +
            "stalls +0 (total 0) | oversized +0 (total 0) | " +
            "gcHeap=128 MB | gcLoad=1024 MB | " +
            "workingSet=2048 MB | privateMem=2048 MB | " +
            "unaccounted=896 MB | gcCollections=[0,0,0]");
        MemoryFidelityCollector.Instance.EndIteration(bytesProcessed: 1024L * 1024 * 1024);

        var loaded = MemoryFidelityCollector.LoadPersistedResults();
        var match = loaded.FirstOrDefault(r => r.BenchmarkName == marker);
        if (match is null) throw new InvalidOperationException(
            $"Memory-fidelity persistence round-trip failed: nothing was loaded back from " +
            $"{MemoryFidelityCollector.SamplesDirectory}. Check IL-baked path and file permissions.");
        const long MB = 1024L * 1024L;
        if (match.PeakWorkingSetBytes != 2048 * MB) throw new InvalidOperationException(
            $"Memory-fidelity persistence round-trip drift on workingSet: expected 2048 MB, got {match.PeakWorkingSetBytes / MB} MB.");
        if (match.BytesProcessed != 1024L * 1024 * 1024) throw new InvalidOperationException(
            $"Memory-fidelity persistence round-trip drift on bytesProcessed: expected 1 GB, got {match.BytesProcessed} bytes.");
    }
}
