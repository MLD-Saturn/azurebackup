using System.Globalization;
using System.Text;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B75: emits a compact, committable markdown summary alongside the
/// BDN-generated CSV/HTML/markdown reports in
/// <c>BenchmarkDotNet.Artifacts/results/</c>. The file is named
/// <c>{BenchmarkType}-summary.md</c> and is OVERWRITTEN on every run
/// so the same path can be committed to the repo and diffed across
/// runs the way the CSV already is.
///
/// <para>
/// Motivation: the timestamped <c>benchmarks/*.log</c> file the user
/// manually tees from <c>dotnet run</c> output is huge (130 KB+ on the
/// memory benchmark), gitignored (the repo's <c>*.log</c> rule), and
/// awkward to diff. The most useful portions live in the BDN
/// <see cref="Summary"/> object already in memory at the end of the
/// run: per-case Mean/StdErr/StdDev, total runtime, and the
/// memory-fidelity columns the
/// <see cref="MemoryFidelityColumnProvider"/> also feeds into the CSV.
/// B75 re-renders those into a single markdown table per benchmark so a
/// post-run diff against the committed file shows exactly what moved.
/// </para>
///
/// <para>
/// <b>What it captures.</b>
/// <list type="bullet">
///   <item>Run timestamp + host OS / .NET version (one line each).</item>
///   <item>Total benchmark count and aggregated wall-clock estimate
///         (sum of per-case Mean × 2 iterations).</item>
///   <item>Per-case table: workload / budget / Mean / StdErr / StdDev +
///         every column the configured <see cref="IColumnProvider"/>s
///         contribute (which includes the fidelity columns when the
///         memory benchmark is run).</item>
///   <item>Outlier callout: any case whose <c>StdErr/Mean</c> exceeds
///         a 5% threshold gets a one-line warning under the table so
///         a noisy iteration is impossible to miss.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>What it does NOT capture.</b> Per-iteration timing, BDN's
/// validation messages, the toolchain restore log -- anything that
/// belongs in the verbose <c>.log</c> rather than a committable
/// diff-friendly summary. Operators who need that level of detail
/// still have the timestamped log.
/// </para>
///
/// <para>
/// <b>Failure mode.</b> The writer NEVER throws out of
/// <see cref="WriteAll"/>; a file-IO or formatting failure on the
/// post-run hook would tear down a multi-hour benchmark with no
/// surviving artifacts. Errors are caught and emitted to
/// <see cref="Console.Error"/> as a single line so the operator
/// still sees them.
/// </para>
/// </summary>
public static class BenchmarkSummaryWriter
{
    /// <summary>
    /// The summary file suffix appended to the benchmark type name.
    /// Pulled out as a constant so the test suite can match the
    /// produced filename without duplicating the format string.
    /// </summary>
    public const string SummaryFileSuffix = "-summary.md";

    /// <summary>
    /// Writes one summary file per summary into
    /// <paramref name="outputDirectory"/>. Existing files at the
    /// target paths are overwritten. Returns the list of files
    /// written so callers can log them.
    /// </summary>
    /// <param name="summaries">
    /// The summaries returned by
    /// <see cref="BenchmarkDotNet.Running.BenchmarkSwitcher.Run"/>.
    /// May be empty (e.g. when the user passed <c>--list</c>).
    /// </param>
    /// <param name="outputDirectory">
    /// Target directory. Created if missing. Typically
    /// <c>BenchmarkDotNet.Artifacts/results</c> so the summary
    /// lands next to the CSV.
    /// </param>
    public static IReadOnlyList<string> WriteAll(
        IEnumerable<Summary> summaries, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var written = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (var summary in summaries)
            {
                try
                {
                    var path = WriteOne(summary, outputDirectory);
                    if (path is not null) written.Add(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[B75] BenchmarkSummaryWriter: failed to write summary for " +
                        $"{summary?.Title ?? "<unknown>"}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[B75] BenchmarkSummaryWriter: top-level failure writing to " +
                $"'{outputDirectory}': {ex.GetType().Name}: {ex.Message}");
        }
        return written;
    }

    /// <summary>
    /// Builds the markdown content for one <see cref="Summary"/> as
    /// a pure string. Exposed for the unit tests so the format can
    /// be asserted without involving the file system.
    /// </summary>
    public static string BuildMarkdown(Summary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new StringBuilder();
        var title = summary.Title ?? "<unnamed-summary>";
        var benchmarkName = ResolveBenchmarkTypeName(summary);

        sb.AppendLine(CultureInfo.InvariantCulture, $"# {benchmarkName}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"_Generated by `BenchmarkSummaryWriter` (B75) at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z._");
        sb.AppendLine();

        var cases = summary.BenchmarksCases.ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Cases: **{cases.Count}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Aggregated wall-clock estimate: **{FormatDuration(EstimateTotalRuntime(summary, cases))}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Title: `{title}`");
        AppendHostInfoLine(sb, summary);
        sb.AppendLine();

        if (cases.Count == 0)
        {
            sb.AppendLine("_No benchmark cases were executed._");
            return sb.ToString();
        }

        AppendCaseTable(sb, summary, cases);
        AppendOutlierCallout(sb, summary, cases);

        return sb.ToString();
    }

    private static string? WriteOne(Summary summary, string outputDirectory)
    {
        if (summary is null) return null;
        var benchmarkName = ResolveBenchmarkTypeName(summary);
        if (string.IsNullOrEmpty(benchmarkName)) return null;

        // Mirror the CSV's naming convention so the summary sorts next
        // to its peers in a directory listing:
        //   AzureBackup.Benchmarks.MemoryBudgetBenchmark-report.csv
        //   AzureBackup.Benchmarks.MemoryBudgetBenchmark-summary.md
        var fileName = $"{benchmarkName}{SummaryFileSuffix}";
        var path = Path.Combine(outputDirectory, fileName);
        var markdown = BuildMarkdown(summary);
        File.WriteAllText(path, markdown);
        return path;
    }

    private static void AppendHostInfoLine(StringBuilder sb, Summary summary)
    {
        // HostEnvironmentInfo is the only summary-attached source of
        // runtime + OS data that survives in the final Summary; the
        // BenchmarkRunInfo is gone by the time we get here.
        var host = summary.HostEnvironmentInfo;
        if (host is null) return;
        try
        {
            var osValue = host.Os?.Value?.ToString() ?? "unknown OS";
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Host: `{host.RuntimeVersion} / {osValue}`");
        }
        catch
        {
            // HostEnvironmentInfo accessors are defensive but the OS
            // detector can throw on exotic hosts; never let it kill
            // the summary.
        }
    }

    private static void AppendCaseTable(StringBuilder sb, Summary summary, List<BenchmarkCase> cases)
    {
        var customColumns = CollectCustomColumns(summary).ToList();
        var paramNames = cases[0].Parameters.Items.Select(p => p.Name).ToList();

        // Header: parameter columns, then Mean/StdErr/StdDev/Allocated, then any custom columns.
        sb.Append("| ");
        foreach (var p in paramNames) sb.Append(CultureInfo.InvariantCulture, $"{p} | ");
        sb.Append("Mean | StdErr | StdDev | Allocated |");
        foreach (var col in customColumns) sb.Append(CultureInfo.InvariantCulture, $" {col.ColumnName} |");
        sb.AppendLine();

        sb.Append("| ");
        foreach (var _ in paramNames) sb.Append("--- | ");
        sb.Append("---: | ---: | ---: | ---: |");
        foreach (var _ in customColumns) sb.Append(" ---: |");
        sb.AppendLine();

        foreach (var bc in cases)
        {
            sb.Append("| ");
            foreach (var p in paramNames)
            {
                var item = bc.Parameters.Items.FirstOrDefault(x => x.Name == p);
                sb.Append(CultureInfo.InvariantCulture, $"{item?.Value ?? "-"} | ");
            }

            var report = FindReport(summary, bc);
            var stats = report?.ResultStatistics;
            sb.Append(CultureInfo.InvariantCulture, $"{FormatNanos(stats?.Mean)} | ");
            sb.Append(CultureInfo.InvariantCulture, $"{FormatNanos(stats?.StandardError)} | ");
            sb.Append(CultureInfo.InvariantCulture, $"{FormatNanos(stats?.StandardDeviation)} | ");

            var allocated = TryGetAllocatedBytes(report);
            sb.Append(CultureInfo.InvariantCulture, $"{FormatBytes(allocated)} |");

            foreach (var col in customColumns)
            {
                string value;
                try { value = col.GetValue(summary, bc) ?? "-"; }
                catch { value = "-"; }
                sb.Append(CultureInfo.InvariantCulture, $" {value} |");
            }
            sb.AppendLine();
        }
    }

    private static void AppendOutlierCallout(StringBuilder sb, Summary summary, List<BenchmarkCase> cases)
    {
        var noisy = new List<(string Label, double Percent)>();
        foreach (var bc in cases)
        {
            var stats = FindReport(summary, bc)?.ResultStatistics;
            if (stats is null || stats.Mean <= 0) continue;
            var percent = stats.StandardError / stats.Mean * 100.0;
            if (percent > 5.0)
            {
                var label = string.Join("/", bc.Parameters.Items.Select(p => p.Value));
                noisy.Add((label, percent));
            }
        }
        if (noisy.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("### Noisy iterations (StdErr/Mean > 5%)");
        sb.AppendLine();
        foreach (var (label, percent) in noisy)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{label}`: {percent:F2}%");
        }
    }

    private static TimeSpan EstimateTotalRuntime(Summary summary, List<BenchmarkCase> cases)
    {
        // BDN runs each case warmupCount + iterationCount times, but
        // the most stable per-case estimate available from the Summary
        // is Mean (the average across the kept iterations). Sum the
        // Means as a lower-bound aggregate; this matches what the user
        // sees in the BDN "// Estimated finish" markers.
        var totalNs = 0.0;
        foreach (var bc in cases)
        {
            var stats = FindReport(summary, bc)?.ResultStatistics;
            if (stats is null) continue;
            totalNs += stats.Mean * (stats.N > 0 ? stats.N : 1);
        }
        if (totalNs <= 0) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(totalNs / 1_000_000.0);
    }

    private static IEnumerable<IColumn> CollectCustomColumns(Summary summary)
    {
        // Only emit columns the configured providers contribute; this
        // automatically picks up the MemoryFidelityColumnProvider when
        // the memory benchmark is run, and contributes nothing extra
        // on benchmarks that don't add a custom provider. We avoid
        // listing the standard Mean/Error/Allocated columns again --
        // they're already in the per-case table header. Column providers
        // are reached via BenchmarkCase.Config.GetColumnProviders() because
        // Summary does not expose them directly; deduplicate across cases
        // by IColumn.Id so a provider registered on every case appears once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bc in summary.BenchmarksCases)
        {
            var providers = bc.Config?.GetColumnProviders();
            if (providers is null) continue;
            foreach (var provider in providers)
            {
                IEnumerable<IColumn> cols;
                try { cols = provider.GetColumns(summary); }
                catch { continue; }
                foreach (var col in cols)
                {
                    if (col.Category != ColumnCategory.Custom) continue;
                    if (!seen.Add(col.Id)) continue;
                    yield return col;
                }
            }
        }
    }

    private static BenchmarkReport? FindReport(Summary summary, BenchmarkCase bc)
    {
        foreach (var r in summary.Reports)
        {
            if (r.BenchmarkCase == bc) return r;
        }
        return null;
    }

    private static long? TryGetAllocatedBytes(BenchmarkReport? report)
    {
        // MemoryDiagnoser reports bytes-allocated-per-operation under
        // the GcStats metric; access via Metrics dictionary so the
        // writer keeps working if the report didn't enable
        // MemoryDiagnoser (the value just stays "-").
        if (report?.Metrics is null) return null;
        foreach (var kv in report.Metrics)
        {
            if (kv.Key.Contains("Allocated", StringComparison.OrdinalIgnoreCase))
            {
                return (long)kv.Value.Value;
            }
        }
        return null;
    }

    internal static string FormatNanos(double? nanos)
    {
        if (nanos is null || double.IsNaN(nanos.Value) || nanos.Value <= 0) return "-";
        var ns = nanos.Value;
        if (ns >= 60_000_000_000) return $"{ns / 60_000_000_000:F2} m";
        if (ns >= 1_000_000_000) return $"{ns / 1_000_000_000:F2} s";
        if (ns >= 1_000_000) return $"{ns / 1_000_000:F2} ms";
        if (ns >= 1_000) return $"{ns / 1_000:F2} us";
        return $"{ns:F2} ns";
    }

    internal static string FormatBytes(long? bytes)
    {
        if (bytes is null || bytes.Value <= 0) return "-";
        var v = bytes.Value;
        if (v >= 1L << 30) return $"{v / (double)(1L << 30):F2} GB";
        if (v >= 1L << 20) return $"{v / (double)(1L << 20):F2} MB";
        if (v >= 1L << 10) return $"{v / (double)(1L << 10):F2} KB";
        return $"{v} B";
    }

    internal static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "-";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:D2}m {span.Seconds:D2}s";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds:D2}s";
        return $"{span.TotalSeconds:F1}s";
    }

    private static string ResolveBenchmarkTypeName(Summary summary)
    {
        // Mirror MemoryFidelityColumnProvider's resolution path so the
        // summary file's name matches the BDN-generated CSV. The
        // first case's Descriptor.Type is what BDN itself uses to
        // build the artifact file names.
        if (summary.BenchmarksCases.Length == 0) return summary.Title ?? "summary";
        var bc = summary.BenchmarksCases[0];
        return bc.Descriptor.Type.FullName ?? bc.Descriptor.Type.Name;
    }
}
