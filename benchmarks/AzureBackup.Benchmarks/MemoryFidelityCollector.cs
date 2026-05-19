using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureBackup.Benchmarks;

/// <summary>
/// W5 Phase 1 (B64): aggregates the per-iteration high-water marks
/// of the memory-fidelity signals emitted by
/// <see cref="AzureBackup.Core.Services.BackupMemoryReporter"/>'s
/// <c>[mem]</c> and <c>[mem-start]</c> log lines.
///
/// <para>
/// Motivation: pre-W5 every backup benchmark reported BDN's <c>Mean</c>
/// and <c>Allocated</c> columns; <c>Allocated</c> is cumulative
/// managed allocations (LOH + non-LOH) and tells us nothing about
/// how accurately the orchestrator's <c>MemoryLimitMB</c> ceiling
/// holds against actual process residency. The user explicitly asked
/// for a way to compare residency BEFORE and AFTER a redesign;
/// without these columns every redesign decision would be
/// faith-based.
/// </para>
///
/// <para>
/// <b>How it works.</b> The benchmark wires a single instance via
/// <see cref="AttachTo"/> to the orchestrator's <c>StatusChanged</c>
/// event (the same sink the production reporter targets in the GUI
/// log pane). Every <c>[mem]</c> or <c>[mem-start]</c> line is
/// parsed for the four numbers we care about (<c>workingSet</c>,
/// <c>used</c>, <c>unaccounted</c>, <c>stalls</c>) plus the budget
/// peak, and the per-iteration high-water marks are recorded into a
/// thread-safe sample list keyed by benchmark + workload + iteration.
/// At the end of the BDN run the
/// <see cref="MemoryFidelityColumnProvider"/> reads back the
/// aggregated samples and surfaces them as BDN columns alongside
/// <c>Mean</c> and <c>Allocated</c>.
/// </para>
///
/// <para>
/// <b>Columns produced</b> (per benchmark + workload + memory-limit
/// param row):
/// <list type="bullet">
///   <item><c>PeakWS_MB</c>: the highest <c>workingSet</c> value
///     observed during the iteration. The single number that says
///     "the process actually held this many MB resident at peak",
///     independent of whether the orchestrator's accounting agreed.</item>
///   <item><c>MaxUnaccounted_MB</c>: the highest <c>unaccounted</c>
///     value observed during the iteration. Pre-W5 this is the
///     dominant signal of how much real residency the budget cannot
///     see; a redesign that closes this gap is what Phase 3 / 4 are
///     supposed to do.</item>
///   <item><c>OvershootRatio</c>: <c>PeakWS_MB / TotalBudget_MB</c>.
///     1.0 means the OS saw exactly the configured ceiling. 2.5 means
///     the budget said 16 GB and the process held 40 GB at peak.
///     This is the headline number to compare designs by; the
///     <see cref="MemoryFidelityColumnProvider"/> formats it to two
///     decimals.</item>
///   <item><c>StallsPerGB</c>: <c>StallCount / GBProcessed</c>. The
///     defence against "the new design just throttles to win the
///     fidelity number" -- a redesign that lowers <c>OvershootRatio</c>
///     by stalling all the time would show up here as a large jump.</item>
///   <item><c>OversizedAdmissions</c>: count of B34 deadlock-avoidance
///     bypasses, surfaced verbatim because every bypass is a moment
///     where the configured ceiling was breached.</item>
///   <item><c>PeakBudgetUsed_MB</c>: the budget's own peak <c>used</c>
///     value, useful as a sanity check that the iteration actually
///     exercised the budget rather than topping out below it.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Concurrency.</b> The collector is a thread-safe singleton.
/// BDN runs benchmark methods serially within an iteration, but the
/// orchestrator under test fires <c>StatusChanged</c> from a
/// <c>Timer</c> callback on a thread-pool thread, so the line-parse
/// path is on a different thread than the benchmark method itself.
/// All mutating operations are guarded by a single lock; the parse
/// itself is allocation-light to keep observer overhead well under
/// 1 percent of the iteration cost.
/// </para>
///
/// <para>
/// <b>Resilience.</b> Lines that fail to parse (foreign log lines
/// that happen to land on the same <c>StatusChanged</c> sink, or
/// future format changes the regex doesn't recognise) are silently
/// ignored. The collector must never throw out of the event handler
/// or it would tear down the benchmark process from a Timer callback.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b> A single instance lives for the BDN run.
/// <see cref="StartIteration"/> resets the per-iteration high-water
/// marks at the top of every benchmark method;
/// <see cref="EndIteration"/> stamps the iteration result into
/// <see cref="Results"/> for the column provider to consume after
/// the run.
/// </para>
/// </summary>
public sealed class MemoryFidelityCollector
{
    private const long MB = 1024L * 1024L;

    /// <summary>
    /// Directory where every process (host + BDN children) writes its
    /// per-iteration JSON-lines fidelity records, and from which the
    /// host's <see cref="MemoryFidelityColumnProvider"/> reads them at
    /// end-of-run. Derived from <see cref="[CallerFilePath]"/> at
    /// compile time so the path is baked into IL: both the BDN host
    /// process and every BDN child process resolve to the SAME
    /// absolute path without any runtime state, env-var inheritance,
    /// or entry-point cooperation.
    /// <para>
    /// <b>Why not env vars (B65, retracted by B66):</b> the original
    /// B65 design published a per-run random temp directory via the
    /// AZBK_BENCH_FIDELITY_DIR env var in <c>Program.Main</c> and
    /// relied on BDN to inherit it into the child. Two failure modes
    /// killed that approach: (1) some launchers (Visual Studio 2026's
    /// benchmark integration, <c>dotnet test</c>, BDN's own re-launch
    /// paths) bypass <c>Program.Main</c> entirely, so the publication
    /// never runs; (2) even when the publication does run,
    /// <c>Environment.SetEnvironmentVariable</c> after process start
    /// is not guaranteed to propagate to children that BDN's default
    /// toolchain spawns via <c>ProcessStartInfo</c>. The first real
    /// benchmark run after B65 created no temp dir at all, confirming
    /// the failure. A compile-time path constant cannot be defeated by
    /// either failure mode.
    /// </para>
    /// </summary>
    public static string SamplesDirectory { get; } = ResolveSamplesDirectory();

    /// <summary>Per-PID JSON-lines filename inside the samples dir.</summary>
    private static string SamplesFileName { get; } =
        $"samples-pid{Environment.ProcessId}.jsonl";

    private static string ResolveSamplesDirectory([CallerFilePath] string thisFile = "")
    {
        // thisFile is baked into IL as the absolute path to this source
        // file at compile time. Its parent is the benchmarks project
        // directory; place the samples folder alongside the source so
        // host and child resolve to the same path regardless of cwd.
        var projectDir = Path.GetDirectoryName(thisFile) ?? Path.GetTempPath();
        var dir = Path.Combine(projectDir, ".fidelity-samples");
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        return dir;
    }

    /// <summary>
    /// Process-wide singleton. BDN constructs benchmark types per
    /// iteration, so a per-instance collector would lose state
    /// between the benchmark method and the column-provider read
    /// at the end of the run.
    /// </summary>
    public static MemoryFidelityCollector Instance { get; } = new();

    private readonly Lock _lock = new();
    private readonly List<MemoryFidelityResult> _results = [];
    private MemoryFidelitySample _current;

    private MemoryFidelityCollector() { }

    /// <summary>
    /// Snapshot of every iteration's aggregated fidelity sample.
    /// Read by <see cref="MemoryFidelityColumnProvider"/> at the end
    /// of the BDN run. The list is appended to under the lock; the
    /// returned array is a defensive copy.
    /// </summary>
    public IReadOnlyList<MemoryFidelityResult> Results
    {
        get
        {
            lock (_lock) { return [.. _results]; }
        }
    }

    /// <summary>
    /// Hook for benchmark methods. Wires an emit-line handler onto
    /// the orchestrator under test; pair with the existing
    /// <c>StatusChanged</c> sink so every line the production
    /// reporter emits flows through the parser. Returns an
    /// <see cref="IDisposable"/> the benchmark stores in its iteration
    /// scope to detach when the iteration ends.
    /// </summary>
    public IDisposable AttachTo(EventHandler<string> hook, Action<EventHandler<string>> attach, Action<EventHandler<string>> detach)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);
        attach(hook);
        return new Detacher(() => detach(hook));
    }

    /// <summary>
    /// Resets the per-iteration high-water marks. Call at the very
    /// top of every benchmark method, BEFORE the first orchestrator
    /// operation runs, so a value left over from the previous
    /// iteration cannot poison the next.
    /// </summary>
    public void StartIteration(string benchmarkName, string workload, int memoryLimitMB)
    {
        lock (_lock)
        {
            _current = new MemoryFidelitySample(benchmarkName, workload, memoryLimitMB);
        }
    }

    /// <summary>
    /// Stamps the current high-water marks into a stable result
    /// record and appends it to <see cref="Results"/>. Call from the
    /// benchmark method's <c>finally</c> block so cancelled or
    /// exception-terminated iterations still record what they
    /// observed.
    /// </summary>
    public void EndIteration(long bytesProcessed)
    {
        MemoryFidelityResult? toPersist = null;
        lock (_lock)
        {
            var sample = _current;
            if (sample.BenchmarkName is null) return;
            var result = new MemoryFidelityResult(
                BenchmarkName: sample.BenchmarkName,
                Workload: sample.Workload,
                MemoryLimitMB: sample.MemoryLimitMB,
                PeakWorkingSetBytes: sample.PeakWorkingSetBytes,
                MaxUnaccountedBytes: sample.MaxUnaccountedBytes,
                PeakBudgetUsedBytes: sample.PeakBudgetUsedBytes,
                StallCount: sample.StallCount,
                OversizedAdmissions: sample.OversizedAdmissions,
                BytesProcessed: bytesProcessed);
            _results.Add(result);
            _current = default;
            toPersist = result;
        }

        // B66: cross-process persistence using a compile-time
        // derived path (see SamplesDirectory xmldoc for why B65's
        // env-var approach was retracted). One file per PID so the
        // host and any number of BDN children write without
        // contending for an exclusive lock. The host reads every
        // samples-pid*.jsonl in the directory at end-of-run.
        if (toPersist is null) return;
        var path = Path.Combine(SamplesDirectory, SamplesFileName);
        try
        {
            var json = JsonSerializer.Serialize(toPersist);
            File.AppendAllText(path, json + Environment.NewLine);
            // Visible in BDN's child stderr so the next time fidelity
            // columns regress to "-" the failure is diagnosable from
            // the run log instead of from a forensic temp-dir hunt.
            Console.Error.WriteLine(
                $"[B66 fidelity persist] pid={Environment.ProcessId} " +
                $"-> {path} (benchmark={toPersist.BenchmarkName}, " +
                $"workload={toPersist.Workload}, " +
                $"memoryLimitMB={toPersist.MemoryLimitMB}, " +
                $"bytesProcessed={toPersist.BytesProcessed})");
        }
        catch (Exception ex)
        {
            // Defensive: never throw out of an event handler. But do
            // surface the failure -- a silent catch is what made B65
            // undebuggable.
            try
            {
                Console.Error.WriteLine(
                    $"[B66 fidelity persist FAILED] pid={Environment.ProcessId} " +
                    $"-> {path}: {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* very best effort */ }
        }
    }

    /// <summary>
    /// Loads every persisted iteration sample from every
    /// <c>samples-pid*.jsonl</c> file under
    /// <see cref="SamplesDirectory"/>. Returns an empty list if the
    /// directory is missing or contains no readable files. Called by
    /// <see cref="MemoryFidelityColumnProvider"/> during the host
    /// process's end-of-run column projection. Reads every PID's
    /// samples (B66) so a benchmark run that spawned multiple BDN
    /// children -- typical for multi-iteration benchmarks -- has all
    /// of them aggregated, not just the first.
    /// </summary>
    public static IReadOnlyList<MemoryFidelityResult> LoadPersistedResults()
    {
        var dir = SamplesDirectory;
        if (!Directory.Exists(dir)) return [];

        var results = new List<MemoryFidelityResult>();
        string[] files;
        try { files = Directory.GetFiles(dir, "samples-pid*.jsonl"); }
        catch { return results; }

        foreach (var path in files)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<MemoryFidelityResult>(line);
                        if (parsed is not null) results.Add(parsed);
                    }
                    catch
                    {
                        // Skip malformed lines: a previous run's partial
                        // write or a future schema change should not
                        // poison the whole table.
                    }
                }
            }
            catch
            {
                // Skip unreadable files; the rest still count.
            }
        }

        // Diagnostic: visible in BDN's host stderr at end-of-run so
        // the next "every column is -" regression is debuggable in
        // one glance.
        try
        {
            Console.Error.WriteLine(
                $"[B66 fidelity load] dir={dir} files={files.Length} " +
                $"records={results.Count}");
        }
        catch { /* best effort */ }

        return results;
    }

    /// <summary>
    /// Parses one line emitted by
    /// <c>BackupMemoryReporter.EmitSample</c>. Public so the column
    /// provider's tests can pin the parse against a recorded line
    /// without having to spin a real backup; the production wiring
    /// goes through the event-handler returned by
    /// <see cref="CreateLineHandler"/>.
    /// </summary>
    public void RecordSampleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!line.StartsWith("[mem", StringComparison.Ordinal)) return;

        try
        {
            var workingSet = ExtractMb(line, WorkingSetRegex);
            var unaccounted = ExtractMb(line, UnaccountedRegex);
            var peakBudget = ExtractMb(line, PeakBudgetRegex);
            var used = ExtractMb(line, UsedBudgetRegex);
            var stalls = ExtractCount(line, StallsTotalRegex);
            var oversized = ExtractCount(line, OversizedTotalRegex);

            lock (_lock)
            {
                if (_current.BenchmarkName is null) return;
                if (workingSet > _current.PeakWorkingSetBytes) _current.PeakWorkingSetBytes = workingSet;
                if (unaccounted > _current.MaxUnaccountedBytes) _current.MaxUnaccountedBytes = unaccounted;
                // Prefer the explicit "peak=" extracted from "(peak=NNN MB)";
                // fall back to the running used value for runs where the
                // peak was not yet emitted (very early in the iteration).
                if (peakBudget > _current.PeakBudgetUsedBytes) _current.PeakBudgetUsedBytes = peakBudget;
                if (used > _current.PeakBudgetUsedBytes) _current.PeakBudgetUsedBytes = used;
                if (stalls > _current.StallCount) _current.StallCount = stalls;
                if (oversized > _current.OversizedAdmissions) _current.OversizedAdmissions = oversized;
            }
        }
        catch
        {
            // Defensive: never throw out of the event handler. A parse
            // failure on a single line just means that line did not
            // contribute to the high-water marks, which is benign.
        }
    }

    /// <summary>
    /// Returns an event-handler that forwards each
    /// <c>StatusChanged</c> line through
    /// <see cref="RecordSampleLine"/>. Used by the benchmark setup
    /// to wire the collector into the orchestrator.
    /// </summary>
    public EventHandler<string> CreateLineHandler() =>
        (_, line) => RecordSampleLine(line);

    private static long ExtractMb(string line, Regex regex)
    {
        var m = regex.Match(line);
        if (!m.Success) return 0L;
        if (!long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
            return 0L;
        return mb * MB;
    }

    private static long ExtractCount(string line, Regex regex)
    {
        var m = regex.Match(line);
        if (!m.Success) return 0L;
        if (!long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return 0L;
        return n;
    }

    // Regexes pinned against the BackupMemoryReporter.EmitSample format:
    //   [mem] backup t+12s | budget used=4096 MB / 8192 MB (50.0%, peak=6144 MB) |
    //   stalls +0 (total 12) | oversized +0 (total 0) | gcHeap=512 MB |
    //   gcLoad=8192 MB | workingSet=10240 MB | privateMem=10240 MB |
    //   unaccounted=2048 MB | gcCollections=[3,1,0] | lohPool=512 MB cached ...
    // Each regex is a small fixed-state automaton compiled once.
    private static readonly Regex WorkingSetRegex = new(@"workingSet=(\d+)\s*MB", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnaccountedRegex = new(@"unaccounted=(\d+)\s*MB", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PeakBudgetRegex = new(@"peak=(\d+)\s*MB", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsedBudgetRegex = new(@"used=(\d+)\s*MB", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StallsTotalRegex = new(@"stalls\s+\+\d+\s+\(total\s+(\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OversizedTotalRegex = new(@"oversized\s+\+\d+\s+\(total\s+(\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Detacher : IDisposable
    {
        private Action? _detach;
        public Detacher(Action detach) { _detach = detach; }
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _detach, null);
            d?.Invoke();
        }
    }

    private struct MemoryFidelitySample
    {
        public string? BenchmarkName;
        public string Workload;
        public int MemoryLimitMB;
        public long PeakWorkingSetBytes;
        public long MaxUnaccountedBytes;
        public long PeakBudgetUsedBytes;
        public long StallCount;
        public long OversizedAdmissions;

        public MemoryFidelitySample(string benchmarkName, string workload, int memoryLimitMB)
        {
            BenchmarkName = benchmarkName;
            Workload = workload;
            MemoryLimitMB = memoryLimitMB;
            PeakWorkingSetBytes = 0;
            MaxUnaccountedBytes = 0;
            PeakBudgetUsedBytes = 0;
            StallCount = 0;
            OversizedAdmissions = 0;
        }
    }
}

/// <summary>
/// Stable per-iteration record produced by
/// <see cref="MemoryFidelityCollector"/>. The
/// <see cref="MemoryFidelityColumnProvider"/> reads these at end of
/// run and aggregates per (benchmark, workload, memory-limit) group.
/// </summary>
public sealed record MemoryFidelityResult(
    string BenchmarkName,
    string Workload,
    int MemoryLimitMB,
    long PeakWorkingSetBytes,
    long MaxUnaccountedBytes,
    long PeakBudgetUsedBytes,
    long StallCount,
    long OversizedAdmissions,
    long BytesProcessed);
