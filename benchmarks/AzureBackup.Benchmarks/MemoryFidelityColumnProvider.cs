using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace AzureBackup.Benchmarks;

/// <summary>
/// W5 Phase 1 (B64): BenchmarkDotNet column provider that surfaces
/// the memory-fidelity high-water marks aggregated by
/// <see cref="MemoryFidelityCollector"/> alongside the standard
/// <c>Mean</c> / <c>Allocated</c> columns.
///
/// <para>
/// Six columns are emitted per benchmark row:
/// <list type="bullet">
///   <item><c>PeakWS_MB</c> -- Process.WorkingSet64 high-water mark.</item>
///   <item><c>MaxUnacc_MB</c> -- max(workingSet - used - poolCached).</item>
///   <item><c>PeakBudget_MB</c> -- MemoryBudget.PeakUsedBytes.</item>
///   <item><c>Overshoot</c> -- PeakWS / TotalBudget (1.0 = honest).</item>
///   <item><c>Stalls/GB</c> -- MemoryBudget.StallCount per GB processed.</item>
///   <item><c>Oversized</c> -- MemoryBudget.OversizedAdmissions.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Aggregation.</b> BDN runs each benchmark method N times per
/// configuration row (warmupCount + iterationCount). The collector
/// records one
/// <see cref="MemoryFidelityResult"/> per iteration; this provider
/// reports the maximum across iterations for the high-water columns
/// (the worst observation is the meaningful one for residency
/// comparisons) and the sum-divided-by-bytes for
/// <c>Stalls/GB</c>.
/// </para>
///
/// <para>
/// <b>Identity matching.</b> BDN identifies a row by
/// <see cref="BenchmarkCase.Descriptor"/> and the parameter values
/// of that case. The collector keys results by <c>BenchmarkName</c>
/// + <c>Workload</c> + <c>MemoryLimitMB</c>, which matches BDN's
/// row identity for every benchmark in this project. Cases without
/// a matching collector entry render as <c>"-"</c> rather than 0
/// so it is visually obvious the iteration didn't actually exercise
/// the fidelity path (e.g. a benchmark that never calls
/// <see cref="MemoryFidelityCollector.StartIteration"/>).
/// </para>
/// </summary>
public sealed class MemoryFidelityColumnProvider : IColumnProvider
{
    /// <summary>Singleton column-provider for use in BDN configs.</summary>
    public static readonly IColumnProvider Instance = new MemoryFidelityColumnProvider();

    public IEnumerable<IColumn> GetColumns(Summary summary) =>
    [
        new FidelityColumn("PeakWS_MB", "Process.WorkingSet64 peak in MB", r =>
            FormatMb(r.PeakWorkingSetBytes)),
        new FidelityColumn("MaxUnacc_MB", "Max (workingSet - used - poolCached) in MB", r =>
            FormatMb(r.MaxUnaccountedBytes)),
        new FidelityColumn("PeakBudget_MB", "MemoryBudget peak used in MB", r =>
            FormatMb(r.PeakBudgetUsedBytes)),
        new FidelityColumn("Overshoot", "PeakWS / configured budget (1.0 = honest)", r =>
            FormatRatio(r.PeakWorkingSetBytes, r.MemoryLimitMB)),
        new FidelityColumn("Stalls/GB", "MemoryBudget.StallCount per GB processed", r =>
            FormatStalls(r.StallCount, r.BytesProcessed)),
        new FidelityColumn("Oversized", "MemoryBudget.OversizedAdmissions", r =>
            r.OversizedAdmissions.ToString()),
    ];

    private const long MB = 1024L * 1024L;
    private const long GB = 1024L * 1024L * 1024L;

    private static string FormatMb(long bytes)
    {
        if (bytes <= 0) return "-";
        return (bytes / MB).ToString();
    }

    private static string FormatRatio(long peakWsBytes, int memoryLimitMB)
    {
        if (peakWsBytes <= 0 || memoryLimitMB <= 0) return "-";
        var budgetBytes = (long)memoryLimitMB * MB;
        return ((double)peakWsBytes / budgetBytes).ToString("F2");
    }

    private static string FormatStalls(long stalls, long bytesProcessed)
    {
        if (bytesProcessed <= 0) return "-";
        var gb = (double)bytesProcessed / GB;
        if (gb < 0.001) return "-";
        return (stalls / gb).ToString("F1");
    }

    private sealed class FidelityColumn : IColumn
    {
        private readonly Func<MemoryFidelityResult, string> _project;

        public FidelityColumn(string id, string legend, Func<MemoryFidelityResult, string> project)
        {
            Id = id;
            ColumnName = id;
            Legend = legend;
            _project = project;
        }

        public string Id { get; }
        public string ColumnName { get; }
        public string Legend { get; }
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;

        public bool IsAvailable(Summary summary) => true;
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
            GetValue(summary, benchmarkCase, SummaryStyle.Default);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var benchmarkName = benchmarkCase.Descriptor.Type.Name;
            var workload = ParameterString(benchmarkCase, "Workload") ?? string.Empty;
            var memoryLimitMB = ParameterInt(benchmarkCase, "MemoryLimitParam") ?? 0;

            // B66: union the in-process singleton (populated when the
            // benchmark runs in-process via --launchCount 0 or
            // InProcessEmitToolchain) with the per-iteration JSON
            // lines every PID (host + BDN children) persisted under
            // MemoryFidelityCollector.SamplesDirectory. The first
            // B64/B65 runs showed every cell as "-" because BDN's
            // default toolchain runs benchmark methods in a child
            // and the host-side column provider saw an empty
            // singleton; B66 replaced the env-var-published temp dir
            // with a compile-time-derived path so host and child
            // always agree without runtime cooperation.
            var matches = MemoryFidelityCollector.Instance.Results
                .Concat(MemoryFidelityCollector.LoadPersistedResults())
                .Where(r =>
                    r.BenchmarkName == benchmarkName &&
                    r.Workload == workload &&
                    r.MemoryLimitMB == memoryLimitMB)
                .ToList();

            if (matches.Count == 0) return "-";

            // Per the column-provider contract: report the worst-case
            // observation (max across iterations) for high-water marks.
            // The aggregator is unaware of the column's own semantics,
            // so we hand it back a synthetic record that carries the
            // per-column extreme and the SUM of bytes processed across
            // iterations (so Stalls/GB averages correctly).
            var aggregated = new MemoryFidelityResult(
                BenchmarkName: benchmarkName,
                Workload: workload,
                MemoryLimitMB: memoryLimitMB,
                PeakWorkingSetBytes: matches.Max(r => r.PeakWorkingSetBytes),
                MaxUnaccountedBytes: matches.Max(r => r.MaxUnaccountedBytes),
                PeakBudgetUsedBytes: matches.Max(r => r.PeakBudgetUsedBytes),
                StallCount: matches.Sum(r => r.StallCount),
                OversizedAdmissions: matches.Max(r => r.OversizedAdmissions),
                BytesProcessed: matches.Sum(r => r.BytesProcessed));

            return _project(aggregated);
        }

        private static string? ParameterString(BenchmarkCase benchmarkCase, string name)
        {
            var item = benchmarkCase.Parameters.Items.FirstOrDefault(p => p.Name == name);
            return item?.Value as string;
        }

        private static int? ParameterInt(BenchmarkCase benchmarkCase, string name)
        {
            var item = benchmarkCase.Parameters.Items.FirstOrDefault(p => p.Name == name);
            return item?.Value as int?;
        }

        public override string ToString() => ColumnName;
    }
}
