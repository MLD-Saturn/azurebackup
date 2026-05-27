using System.Diagnostics;

namespace AzureBackup.Core.Services;

/// <summary>
/// B36: lightweight periodic memory observer that samples the live memory
/// state of a backup operation and emits a one-line structured report on a
/// fixed cadence.
///
/// <para>
/// Motivation: pre-B36 the backup hot path emitted no live memory data.
/// <see cref="MemoryBudget.StallCount"/> was reported once at operation
/// completion, which is too late to diagnose a runaway-residency
/// regression -- by the time the operation finishes the OS may already
/// have killed the process or driven the host into swap. The B30/B33
/// accounting fixes are also unverifiable without a live read of the
/// budget against the actual process working set.
/// </para>
///
/// <para>
/// Each emitted line includes:
/// <list type="bullet">
///   <item><c>budget</c>: <see cref="MemoryBudget.UsedBytes"/> /
///     <see cref="MemoryBudget.TotalBytes"/></item>
///   <item><c>stalls</c>: count of <see cref="MemoryBudget.AcquireAsync"/>
///     calls that took the slow path (incremental since the previous
///     sample, so the rate is visible)</item>
///   <item><c>oversized</c>: count of admissions that bypassed the cap
///     because a single request exceeded the entire budget (B34)</item>
///   <item><c>gcHeap</c>: <see cref="GC.GetTotalMemory"/></item>
///   <item><c>gcLoad</c>: <see cref="GCMemoryInfo.MemoryLoadBytes"/>
///     (the OS-level memory pressure the GC is observing)</item>
///   <item><c>workingSet</c>: <see cref="Process.WorkingSet64"/>
///     (the actual RSS the OS bills the process for)</item>
///   <item><c>privateMem</c>: <see cref="Process.PrivateMemorySize64"/></item>
///   <item><c>gcCollections</c>: gen-0/1/2 collection counts since the
///     start of this reporter, comma-separated</item>
/// </list>
/// </para>
///
/// <para>
/// The <c>workingSet</c> minus the budget's <c>UsedBytes</c> approximates
/// the unaccounted-for residency. A growing gap during a backup is the
/// signature of an undercounted allocation site, which is exactly what
/// B30/B33 are meant to fix. A small bounded gap is normal (GC heap
/// includes managed object overhead, the SqliteBackend's connection
/// pool, the file-watcher state, etc.).
/// </para>
///
/// <para>
/// Sampling cost: a single <see cref="GC.GetGCMemoryInfo"/> +
/// <see cref="Process.GetCurrentProcess"/> call (sub-millisecond) per
/// sample, plus one event invocation. The default 30 s cadence makes the
/// per-operation cost negligible.
/// </para>
///
/// <para>
/// Lifecycle: dispose stops the timer and emits one final sample so the
/// caller sees the post-operation state alongside the periodic ones.
/// </para>
/// </summary>
public sealed class BackupMemoryReporter : IDisposable
{
    private const long MB = 1024L * 1024;

    private readonly MemoryBudget _budget;
    private readonly string _opLabel;
    private readonly Action<string> _emit;
    private readonly TimeSpan _interval;
    private readonly Process _process;
    private readonly Timer _timer;
    private readonly Stopwatch _stopwatch;
    private readonly int _gen0Start;
    private readonly int _gen1Start;
    private readonly int _gen2Start;
    private readonly ChunkBufferPool? _largeChunkPool;
    private readonly ChunkBufferPool? _smallChunkPool;

    private long _previousStallCount;
    private long _previousOversizedCount;
    private int _disposed;

    /// <summary>
    /// Creates a reporter that samples on <paramref name="interval"/> and
    /// emits one structured line per sample by invoking <paramref name="emit"/>.
    /// </summary>
    /// <param name="budget">The shared budget to read from.</param>
    /// <param name="opLabel">
    /// Short label inserted at the start of every line so multi-operation
    /// logs can be filtered (e.g. <c>"backup"</c>, <c>"mirror"</c>,
    /// <c>"restore"</c>).
    /// </param>
    /// <param name="emit">
    /// Sink that receives the formatted line. Typically wired to
    /// <c>BackupOrchestrator.StatusChanged</c> so the user-visible log pane
    /// receives the data without depending on the <c>DIAGNOSTICLOG</c>
    /// build flag.
    /// </param>
    /// <param name="interval">
    /// Sampling cadence. Defaults to 30 seconds; pass a shorter value
    /// when reproducing a regression locally.
    /// </param>
    public BackupMemoryReporter(
        MemoryBudget budget,
        string opLabel,
        Action<string> emit,
        TimeSpan? interval = null,
        ChunkBufferPool? largeChunkPool = null,
        ChunkBufferPool? smallChunkPool = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(opLabel);
        ArgumentNullException.ThrowIfNull(emit);

        _budget = budget;
        _opLabel = opLabel;
        _emit = emit;
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _largeChunkPool = largeChunkPool;
        _smallChunkPool = smallChunkPool;

        // Cache the Process handle. Process.GetCurrentProcess() allocates
        // and snapshots OS-level state on each call; reusing the handle
        // and calling .Refresh() per sample keeps the per-sample cost in
        // the microsecond range. Refresh is required because the cached
        // counters do NOT update automatically.
        _process = Process.GetCurrentProcess();

        _stopwatch = Stopwatch.StartNew();
        _gen0Start = GC.CollectionCount(0);
        _gen1Start = GC.CollectionCount(1);
        _gen2Start = GC.CollectionCount(2);

        // Emit one line immediately so the operation's starting memory
        // state is captured even for short operations that finish before
        // the first periodic tick.
        EmitSample(initial: true);

        _timer = new Timer(OnTick, state: null, _interval, _interval);
    }

    private void OnTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            EmitSample(initial: false);
        }
        catch
        {
            // Defensive: the reporter must never throw out of a Timer
            // callback (an unobserved exception there would tear down the
            // process via TaskScheduler.UnobservedTaskException). Swallow
            // any sampling failure -- the operation it is observing is
            // unaffected.
        }
    }

    private void EmitSample(bool initial)
    {
        _process.Refresh();
        var gcInfo = GC.GetGCMemoryInfo();
        var gcHeap = GC.GetTotalMemory(forceFullCollection: false);

        var stalls = _budget.StallCount;
        var oversized = _budget.OversizedAdmissions;

        var stallDelta = stalls - Interlocked.Exchange(ref _previousStallCount, stalls);
        var oversizedDelta = oversized - Interlocked.Exchange(ref _previousOversizedCount, oversized);

        var gen0Delta = GC.CollectionCount(0) - _gen0Start;
        var gen1Delta = GC.CollectionCount(1) - _gen1Start;
        var gen2Delta = GC.CollectionCount(2) - _gen2Start;

        var used = _budget.UsedBytes;
        var peakUsed = _budget.PeakUsedBytes;
        var total = _budget.TotalBytes;
        var workingSet = _process.WorkingSet64;
        var privateMem = _process.PrivateMemorySize64;
        var elapsed = _stopwatch.Elapsed;

        // B56 (W3 Phase F): post-B55 the budget includes Azure SDK
        // upload staging, so the unaccounted gap (workingSet - used)
        // now reflects only the residency the budget genuinely cannot
        // see (managed-object overhead, file-watcher state, the
        // SqliteBackend connection pool, the LOH recycler's cached
        // buffers if a pool is wired). To make the LOH-pool component
        // explicit rather than hide it inside unaccounted, the line
        // also reports the pool's current and peak cached residency
        // when a pool is wired; the unaccounted figure subtracts the
        // pool residency so the residual is the truly-uncovered
        // remainder. Negative values can occur briefly when the
        // budget was just charged but the allocation has not yet
        // caused a working-set bump; treat as 0.
        // B69 (W5 Phase 3 Commit 1): the same subtraction now also
        // accounts for the small-chunk pool's cached residency so the
        // unaccounted residual stays a true "uncovered" figure after
        // the small-chunk path migrates off ArrayPool<byte>.Shared.
        // B74 (W5 Phase 4 Commit 3, Fix A): formula extracted to
        // ComputeUnaccountedBytes so the invariant can be pinned by
        // a focused unit test without depending on the noisy
        // Process.WorkingSet64 reading. The change in semantics is
        // the post-B72 retention attribution: _budget.UsedBytes now
        // ALREADY contains the pool's cached retention via the
        // ChargeRetention seam, so the pre-B74 formula
        // (workingSet - used - poolCached - smallPoolCached) was
        // double-subtracting the pool cache and producing an
        // artificially small unaccounted reading.
        var poolCachedNow = _largeChunkPool?.TotalBytesCached ?? 0L;
        var smallPoolCachedNow = _smallChunkPool?.TotalBytesCached ?? 0L;
        var poolBudgetCharged = (_largeChunkPool?.BudgetChargedBytes ?? 0L)
                              + (_smallChunkPool?.BudgetChargedBytes ?? 0L);
        var unaccounted = ComputeUnaccountedBytes(workingSet, used, poolBudgetCharged,
            poolCachedNow, smallPoolCachedNow);

        var budgetText = _budget.IsUnlimited
            ? $"used={used / MB} MB / unlimited (peak={peakUsed / MB} MB)"
            : $"used={used / MB} MB / {total / MB} MB ({(double)used / total * 100:F1}%, peak={peakUsed / MB} MB)";

        var prefix = initial ? "[mem-start]" : "[mem]";
        var poolText = _largeChunkPool != null
            ? $" | lohPool={_largeChunkPool.TotalBytesCached / MB} MB cached" +
              $" (peak={_largeChunkPool.PeakBytesCached / MB} MB," +
              $" dropped={_largeChunkPool.TotalReturnsDroppedForCap}," +
              $" hit={_largeChunkPool.HitRate:P0})"
            : string.Empty;
        var smallPoolText = _smallChunkPool != null
            ? $" | smPool={_smallChunkPool.TotalBytesCached / MB} MB cached" +
              $" (peak={_smallChunkPool.PeakBytesCached / MB} MB," +
              $" dropped={_smallChunkPool.TotalReturnsDroppedForCap}," +
              $" hit={_smallChunkPool.HitRate:P0})"
            : string.Empty;
        var line =
            $"{prefix} {_opLabel} t+{elapsed.TotalSeconds:F0}s | budget {budgetText} | " +
            $"stalls +{stallDelta} (total {stalls}) | oversized +{oversizedDelta} (total {oversized}) | " +
            $"gcHeap={gcHeap / MB} MB | gcLoad={gcInfo.MemoryLoadBytes / MB} MB | " +
            $"workingSet={workingSet / MB} MB | privateMem={privateMem / MB} MB | " +
            $"unaccounted={unaccounted / MB} MB | gcCollections=[{gen0Delta},{gen1Delta},{gen2Delta}]" +
            poolText +
            smallPoolText;

        _emit(line);
    }

    /// <summary>
    /// B74 (W5 Phase 4 Commit 3, Fix A): pure formula extracted from
    /// <see cref="EmitSample"/> so the invariant can be pinned by a
    /// focused unit test independent of <see cref="Process.WorkingSet64"/>'s
    /// jitter.
    /// </summary>
    /// <param name="workingSet">Process working-set bytes.</param>
    /// <param name="budgetUsedBytes">
    /// <see cref="MemoryBudget.UsedBytes"/>. Post-B72 this INCLUDES the
    /// pool's cached retention via the <c>ChargeRetention</c> seam, which
    /// is exactly why the pre-B74 formula's third subtraction was a
    /// double-subtract.
    /// </param>
    /// <param name="poolBudgetChargedBytes">
    /// Sum of <c>BudgetChargedBytes</c> across every pool wired into the
    /// reporter. Subtracted from <paramref name="budgetUsedBytes"/> first
    /// so the remaining <c>usedExcludingPoolRetention</c> tracks only the
    /// in-flight allocations; the pool's cached bytes are then put back
    /// in their correct bucket exactly once via
    /// <paramref name="largePoolCachedBytes"/> +
    /// <paramref name="smallPoolCachedBytes"/>.
    /// </param>
    /// <param name="largePoolCachedBytes">
    /// <see cref="ChunkBufferPool.TotalBytesCached"/> for the large pool, 0 when unwired.
    /// </param>
    /// <param name="smallPoolCachedBytes">
    /// <see cref="ChunkBufferPool.TotalBytesCached"/> for the small pool, 0 when unwired.
    /// </param>
    /// <returns>
    /// The unaccounted-residency estimate in bytes, clamped to non-negative.
    /// When no pool is wired and <paramref name="poolBudgetChargedBytes"/> is 0,
    /// the formula reduces to the pre-B72 shape so non-pool callers are
    /// completely unaffected.
    /// </returns>
    internal static long ComputeUnaccountedBytes(
        long workingSet, long budgetUsedBytes, long poolBudgetChargedBytes,
        long largePoolCachedBytes, long smallPoolCachedBytes)
    {
        var usedExcludingPoolRetention = Math.Max(0, budgetUsedBytes - poolBudgetChargedBytes);
        return Math.Max(0, workingSet - usedExcludingPoolRetention - largePoolCachedBytes - smallPoolCachedBytes);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();

        // Final sample on dispose so the operation's terminal memory
        // state is part of the same log stream.
        try
        {
            EmitSample(initial: false);
        }
        catch
        {
            // Same rationale as OnTick.
        }

        _process.Dispose();
    }
}
