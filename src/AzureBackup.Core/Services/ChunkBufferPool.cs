using System.Collections.Concurrent;

namespace AzureBackup.Core.Services;

/// <summary>
/// B70 (W5 Phase 3 Commit 2): bounded, budget-aware recycler for the
/// <c>byte[]</c> chunk-payload allocations that
/// <see cref="ChunkingService"/> hands to the upload pipeline. A
/// single pool implementation services BOTH the small-chunk path
/// (64 KB - 16 MB, formerly the <c>BudgetedMemoryPool</c>) and the
/// large-chunk path (16 MB - 256 MB, formerly the
/// <c>LargeChunkBufferPool</c>); the bucket geometry is supplied at
/// construction time via <see cref="SmallChunkBucketSizes"/> or
/// <see cref="LargeChunkBucketSizes"/>. The two pre-B70 classes had
/// byte-identical bodies except for the bucket array, so collapsing
/// them into one parameterized type removes ~430 lines of duplication
/// and gives every future tuning change (cap policy, peak telemetry,
/// zero-on-return) a single place to live.
///
/// <para>
/// Motivation (small-chunk path, formerly <c>BudgetedMemoryPool</c>,
/// B69): the W5 Phase 2 measurement baseline confirmed the
/// pre-B69 path leaked residency through
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/>'s per-core tier
/// caches. ArrayPool keeps a per-core (and per-tier) list of recently
/// returned arrays that lives entirely outside the active
/// <see cref="MemoryBudget"/>; on a long-running multi-file backup
/// that retention stacks across every concurrent file worker and
/// forms the bulk of the pre-B69 <c>unaccounted</c> residency the
/// <see cref="MemoryFidelityCollector"/> reported.
/// </para>
///
/// <para>
/// Motivation (large-chunk path, formerly <c>LargeChunkBufferPool</c>,
/// B37): B33 swapped
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> rentals for exact
/// <c>new byte[]</c> allocations on the large-chunk path because the
/// shared pool's per-core caches retain large arrays indefinitely.
/// The exact-allocation path closed that gap but introduced a
/// different one: every chunk's payload buffer became an LOH-resident
/// <c>byte[]</c> that the GC can only reclaim during a gen-2
/// collection. Under the production workload (many concurrent
/// 16-128 MB chunks), gen-2 collections fire roughly once a minute,
/// and ~6 GB of dead-but-unreclaimed LOH accumulates between
/// collections. On a 32 GB host with a 16 GB budget that residency
/// lives entirely outside the budget's accounting and pushes
/// <c>privateMem</c> well past physical RAM.
/// </para>
///
/// <para>
/// Solution shape (both paths): operation-scoped pool whose retention
/// IS the budget's residency. Rented buffers are alive forever (the
/// pool retains them in its bucket bags) so the GC never needs to
/// reclaim them between gen-2 collections; the per-bucket cap and
/// global byte cap together form the strict residency ceiling.
/// Disposing the pool drains every bucket and lets the GC reclaim the
/// retained arrays once the operation completes.
/// </para>
///
/// <para>
/// Bucketing: requests are rounded up to the next configured bucket
/// size and indexed into a fixed set of buckets. Sizes outside that
/// range allocate fresh and do not flow through the pool. The two
/// production geometries -- <see cref="SmallChunkBucketSizes"/>
/// (64 KB / 256 KB / 1 MB / 4 MB / 16 MB) and
/// <see cref="LargeChunkBucketSizes"/> (16 MB / 32 MB / 64 MB /
/// 128 MB / 256 MB) -- partition the buffer-size axis at
/// <see cref="ChunkingService.PoolSkipThresholdBytes"/> (16 MB) so
/// the small pool and the large pool never compete for the same
/// allocation.
/// </para>
///
/// <para>
/// Concurrency: each bucket is a <see cref="ConcurrentBag{T}"/>
/// guarded by an <see cref="Interlocked"/>-managed count. Under
/// contention a bucket's count can briefly exceed the cap (the count
/// is decremented AFTER bag.TryTake; another thread can rent first),
/// but the cap is only an advisory ceiling -- a small overshoot has
/// no correctness implication, only a short-term residency overshoot.
/// The alternative of a strict lock-protected count would serialize
/// all rents and starve the producers.
/// </para>
///
/// <para>
/// Interaction with <see cref="MemoryBudget"/>: the pool does NOT
/// itself charge the budget. The producer in
/// <see cref="ChunkingService"/> charges the rent's bucket ceiling on
/// acquire and releases the same amount on consumer completion; the
/// pool is a pure allocation cache underneath. The budget remains
/// the single throttle.
/// </para>
///
/// <para>
/// Lifetime: the pool is a singleton per backup operation, owned by
/// <see cref="BackupOrchestrator"/>. Disposing the pool clears every
/// bucket and lets the GC reclaim the cached buffers; no explicit
/// finalizer is needed because the cached buffers are reachable only
/// through the pool's bucket arrays.
/// </para>
/// </summary>
public sealed class ChunkBufferPool : IDisposable
{
    private const int KB = 1024;
    private const int MB = 1024 * 1024;

    /// <summary>
    /// Production bucket geometry for the SMALL-chunk path (formerly
    /// the <c>BudgetedMemoryPool</c> default). Range starts at 64 KB
    /// (the smallest payload size CDC tail emission can produce) and
    /// ends at exactly 16 MB, matching
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> so any
    /// chunk at or above the threshold flows to a pool constructed
    /// with <see cref="LargeChunkBucketSizes"/> instead and the two
    /// pools partition the buffer-size axis without overlap.
    /// </summary>
    public static readonly int[] SmallChunkBucketSizes =
    {
        64 * KB,
        256 * KB,
        1 * MB,
        4 * MB,
        16 * MB
    };

    /// <summary>
    /// Production bucket geometry for the LARGE-chunk path (formerly
    /// the <c>LargeChunkBufferPool</c> default). Range covers
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> (16 MB)
    /// up to the worst-case encrypt-buffer tier ceiling for a 128 MB
    /// chunk (256 MB).
    /// </summary>
    public static readonly int[] LargeChunkBucketSizes =
    {
        16 * MB,
        32 * MB,
        64 * MB,
        128 * MB,
        256 * MB
    };

    /// <summary>
    /// Per-bucket capacity in number of cached buffers.
    /// <para>
    /// Sizing rationale: with B27's 16-way file concurrency x 6-way
    /// chunk concurrency = 96 in-flight chunks worst case, and most
    /// chunks landing in one or two buckets on the default
    /// <see cref="ChunkingService"/> config, a per-bucket cap of 32
    /// covers roughly one third of the worst-case in-flight count
    /// without unbounded growth. The bucket caps multiply against
    /// the configured bucket array to give the worst-case pool
    /// residency; production callers further bound the total via
    /// <see cref="MaxCachedBytes"/>.
    /// </para>
    /// <para>
    /// Tuning knob: if the production memory-log shows the pool's
    /// residency saturating one bucket consistently, raise that
    /// bucket's cap. If the log shows the pool barely populated, the
    /// per-bucket cap is too high and can be lowered without losing
    /// the recycle benefit.
    /// </para>
    /// </summary>
    private const int PerBucketCap = 32;

    private readonly int[] _bucketSizes;
    private readonly ConcurrentBag<byte[]>[] _buckets;
    private readonly int[] _bucketCounts;
    private readonly long _maxCachedBytes;
    private readonly MemoryBudget? _retentionBudget;
    private long _totalBytesCached;
    private long _peakBytesCached;
    private long _budgetChargedBytes;
    private long _totalRents;
    private long _totalRentsFromPool;
    private long _totalReturns;
    private long _totalReturnsAccepted;
    private long _totalReturnsDroppedForCap;
    private int _disposed;

    /// <summary>
    /// Creates a new, empty pool with the given bucket geometry and
    /// no global byte cap (only the per-bucket cap applies).
    /// Equivalent to <c>new ChunkBufferPool(bucketSizes, long.MaxValue)</c>.
    /// Use one of the well-known geometries on this class
    /// (<see cref="SmallChunkBucketSizes"/> or
    /// <see cref="LargeChunkBucketSizes"/>) unless you have a
    /// measured reason to deviate.
    /// </summary>
    /// <param name="bucketSizes">
    /// Bucket sizes, smallest first, strictly increasing, all
    /// positive. The array is copied defensively so callers may
    /// reuse the supplied array.
    /// </param>
    public ChunkBufferPool(int[] bucketSizes) : this(bucketSizes, long.MaxValue, retentionBudget: null)
    {
    }

    /// <summary>
    /// Creates a new, empty pool with the given bucket geometry whose
    /// total cached residency across all buckets is bounded by
    /// <paramref name="maxCachedBytes"/>. When a <see cref="Return"/>
    /// would push <see cref="TotalBytesCached"/> above the cap the
    /// buffer is dropped on the floor (the GC reclaims it) instead of
    /// being cached, mirroring the per-bucket overflow behaviour.
    /// The per-bucket cap (<see cref="PerBucketCap"/>) still applies
    /// independently.
    /// <para>
    /// Production callers in <see cref="BackupOrchestrator"/> derive
    /// the cap from the active <see cref="MemoryBudget"/> so the
    /// pool's hidden residency cannot drift past a fraction of the
    /// configured memory limit. Passing <see cref="long.MaxValue"/>
    /// disables the global cap.
    /// </para>
    /// </summary>
    /// <param name="bucketSizes">
    /// Bucket sizes, smallest first, strictly increasing, all
    /// positive. The array is copied defensively so callers may
    /// reuse the supplied array.
    /// </param>
    /// <param name="maxCachedBytes">
    /// Maximum total bytes the pool may keep cached across every
    /// bucket. Must be positive. Pass <see cref="long.MaxValue"/>
    /// to disable the global cap.
    /// </param>
    public ChunkBufferPool(int[] bucketSizes, long maxCachedBytes)
        : this(bucketSizes, maxCachedBytes, retentionBudget: null)
    {
    }

    /// <summary>
    /// B72 (W5 Phase 4): full constructor that additionally attributes
    /// every accepted <see cref="Return"/> (and the matching
    /// <see cref="Rent"/>-from-cache) to <paramref name="retentionBudget"/>
    /// so the pool's cached bytes show up inside
    /// <see cref="MemoryBudget.UsedBytes"/> and
    /// <see cref="MemoryBudget.PeakUsedBytes"/> instead of leaking into
    /// the pre-B72 <c>unaccounted</c> residency gap that
    /// <see cref="MemoryFidelityCollector"/> reported on the 16 GB-budget
    /// adversarial cells.
    /// <para>
    /// Charge semantics: when a <see cref="Return"/> is accepted into a
    /// bucket the pool charges <c>bucketSize</c> against the budget; when
    /// a <see cref="Rent"/> serves a cached buffer it releases the same
    /// amount. Charging uses a non-blocking
    /// <see cref="MemoryBudget.ChargeRetention"/> call that may
    /// temporarily push <c>UsedBytes</c> above the budget total without
    /// blocking the consumer; the cap policy on the pool itself
    /// (<see cref="MaxCachedBytes"/>) is the hard ceiling that keeps the
    /// momentary overshoot small. Producers reading the budget for their
    /// own <see cref="MemoryBudget.AcquireAsync"/> calls will then stall
    /// until enough rents have drained the pool's retention -- which is
    /// exactly the back-pressure shape we want.
    /// </para>
    /// <para>
    /// Pass <see langword="null"/> for <paramref name="retentionBudget"/>
    /// to skip budget attribution and preserve the pre-B72 behaviour
    /// (rent/return remain free and the pool's residency continues to
    /// live outside the budget). The unit-test ChunkBufferPoolTests
    /// suite and any non-production caller that does not own a
    /// <see cref="MemoryBudget"/> should use this overload with
    /// <see langword="null"/>.
    /// </para>
    /// </summary>
    /// <param name="bucketSizes">
    /// Bucket sizes, smallest first, strictly increasing, all
    /// positive. The array is copied defensively so callers may
    /// reuse the supplied array.
    /// </param>
    /// <param name="maxCachedBytes">
    /// Maximum total bytes the pool may keep cached across every
    /// bucket. Must be positive. Pass <see cref="long.MaxValue"/>
    /// to disable the global cap.
    /// </param>
    /// <param name="retentionBudget">
    /// Optional budget against which to charge the pool's cached
    /// retention. The pool does not take ownership of the budget;
    /// callers continue to dispose it via the existing operation-scope
    /// <c>using</c>.
    /// </param>
    public ChunkBufferPool(int[] bucketSizes, long maxCachedBytes, MemoryBudget? retentionBudget)
    {
        ArgumentNullException.ThrowIfNull(bucketSizes);
        if (bucketSizes.Length == 0)
            throw new ArgumentException("Bucket size array must not be empty.", nameof(bucketSizes));
        for (int i = 0; i < bucketSizes.Length; i++)
        {
            if (bucketSizes[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(bucketSizes), "All bucket sizes must be positive.");
            if (i > 0 && bucketSizes[i] <= bucketSizes[i - 1])
                throw new ArgumentException("Bucket sizes must be strictly increasing.", nameof(bucketSizes));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedBytes);

        _bucketSizes = (int[])bucketSizes.Clone();
        _maxCachedBytes = maxCachedBytes;
        _retentionBudget = retentionBudget;
        _buckets = new ConcurrentBag<byte[]>[_bucketSizes.Length];
        _bucketCounts = new int[_bucketSizes.Length];
        for (int i = 0; i < _bucketSizes.Length; i++)
            _buckets[i] = new ConcurrentBag<byte[]>();
    }

    /// <summary>
    /// Snapshot of the configured bucket geometry, smallest first.
    /// Returns a defensive copy; mutating the returned array has no
    /// effect on the pool.
    /// </summary>
    public int[] BucketSizes => (int[])_bucketSizes.Clone();

    /// <summary>
    /// Maximum total bytes the pool may keep cached across all
    /// buckets. Returns <see cref="long.MaxValue"/> when no global
    /// cap was configured.
    /// </summary>
    public long MaxCachedBytes => _maxCachedBytes;

    /// <summary>
    /// Number of returned buffers dropped because accepting them
    /// would have pushed <see cref="TotalBytesCached"/> above
    /// <see cref="MaxCachedBytes"/>. A non-zero value confirms the
    /// global cap is binding; when this is consistently zero the
    /// pool's residency is under the cap and the cap is not the
    /// limiting factor.
    /// </summary>
    public long TotalReturnsDroppedForCap => Volatile.Read(ref _totalReturnsDroppedForCap);

    /// <summary>
    /// Total bytes currently cached across all buckets. Snapshot
    /// only; useful for diagnostics and the B36 memory-log emitter.
    /// </summary>
    public long TotalBytesCached => Volatile.Read(ref _totalBytesCached);

    /// <summary>
    /// High-water mark of <see cref="TotalBytesCached"/> over the
    /// lifetime of this pool. Updated on every accepted
    /// <see cref="Return"/> via a lock-free CAS-max loop. Surfaced
    /// through <see cref="BackupMemoryReporter"/> so the operator can
    /// see whether the pool's cap was actually approached during the
    /// operation, distinct from the instantaneous current cached
    /// bytes (which can have been drained by a recent rent).
    /// </summary>
    public long PeakBytesCached => Volatile.Read(ref _peakBytesCached);

    /// <summary>Number of rent calls (pool-served + fresh-allocation).</summary>
    public long TotalRents => Volatile.Read(ref _totalRents);

    /// <summary>Number of rent calls served from the pool's cached buffers.</summary>
    public long TotalRentsFromPool => Volatile.Read(ref _totalRentsFromPool);

    /// <summary>Number of return calls.</summary>
    public long TotalReturns => Volatile.Read(ref _totalReturns);

    /// <summary>Number of return calls that were actually cached (vs dropped on the floor).</summary>
    public long TotalReturnsAccepted => Volatile.Read(ref _totalReturnsAccepted);

    /// <summary>
    /// B72: total bytes currently charged against the optional
    /// <see cref="MemoryBudget"/> supplied at construction. Equal to
    /// <see cref="TotalBytesCached"/> in steady state; the two can
    /// briefly diverge under contention because the bucket-count
    /// gate and the budget charge are not atomic together, but every
    /// accepted Return / cache-served Rent pair eventually balances
    /// out. Always zero when no retention budget was supplied.
    /// </summary>
    public long BudgetChargedBytes => Volatile.Read(ref _budgetChargedBytes);

    /// <summary>
    /// Pool hit rate as a fraction in [0, 1]. Returns 0 when no
    /// rents have happened yet. A hit rate near 1 means the pool is
    /// doing its job; near 0 means most rents are still allocating
    /// fresh and the pool is providing little residency benefit.
    /// </summary>
    public double HitRate
    {
        get
        {
            var total = TotalRents;
            if (total == 0) return 0.0;
            return (double)TotalRentsFromPool / total;
        }
    }

    /// <summary>
    /// Rents a buffer of at least <paramref name="minimumLength"/>
    /// bytes. Returns a tuple of (buffer, fromPool). When
    /// <c>fromPool</c> is <c>true</c> the returned buffer came from
    /// the pool's cache; when <c>false</c> the request size was
    /// outside the pool's bucket range or the bucket was empty and a
    /// fresh <c>byte[]</c> was allocated (which the caller MUST
    /// still pass back to <see cref="Return"/> so the pool can decide
    /// whether to keep it).
    /// </summary>
    /// <param name="minimumLength">
    /// Minimum required length; the returned buffer's <c>Length</c>
    /// equals the next bucket size at or above this value, or
    /// <paramref name="minimumLength"/> exactly when the request
    /// falls outside the bucket range.
    /// </param>
    public (byte[] Buffer, bool FromPool) Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Interlocked.Increment(ref _totalRents);

        var bucketIndex = GetBucketIndex(minimumLength);
        if (bucketIndex < 0)
        {
            // Outside the pool's bucket range -- allocate fresh.
            return (new byte[minimumLength], false);
        }

        var bucketSize = _bucketSizes[bucketIndex];
        if (_buckets[bucketIndex].TryTake(out var cached))
        {
            // Decrement BEFORE marking the rent as pool-served so the
            // count never sits below zero from a transient ordering.
            Interlocked.Decrement(ref _bucketCounts[bucketIndex]);
            Interlocked.Add(ref _totalBytesCached, -bucketSize);
            // B72: release the retention attribution for this buffer
            // BEFORE returning it to the caller. The caller's own
            // AcquireAsync charge (from ChunkingService or restore-side
            // batch code) is independent of this attribution; releasing
            // here frees the headroom we held while the buffer was
            // sitting in the cache.
            ReleaseRetentionCharge(bucketSize);
            Interlocked.Increment(ref _totalRentsFromPool);
            return (cached, true);
        }

        // Bucket is empty -- allocate fresh at the bucket's full
        // size (not minimumLength) so the eventual Return call can
        // match a bucket. Returning a smaller-than-bucket array would
        // force a bucket-size mismatch on Return, leaking the buffer
        // out of the pool's recycle path.
        return (new byte[bucketSize], false);
    }

    /// <summary>
    /// Returns a buffer to the pool. The buffer is cached IF its
    /// length matches a bucket size, the bucket is below its
    /// per-bucket cap, AND accepting it would not push the pool's
    /// total residency above <see cref="MaxCachedBytes"/>; otherwise
    /// the buffer is dropped on the floor (the GC will reclaim it).
    /// The combined per-bucket and global caps are the back-pressure
    /// mechanism that keeps the pool's total residency bounded.
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Interlocked.Increment(ref _totalReturns);

        // Disposed pools accept Return calls silently so callers in a
        // shutdown race do not see exceptions; the GC will reclaim
        // the buffer.
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var bucketIndex = GetBucketIndexForExactSize(buffer.Length);
        if (bucketIndex < 0)
        {
            // Buffer length does not match any bucket -- this can
            // happen if Rent allocated outside the bucket range, or
            // if a caller misuses the API by returning a
            // non-pool-shaped array. Drop the buffer; the GC will
            // reclaim it.
            return;
        }

        var bucketSize = _bucketSizes[bucketIndex];

        // Global cap: refuse to cache the buffer when accepting it
        // would push the pool's total residency above
        // _maxCachedBytes. The check is a snapshot read against a
        // possibly-stale total; under contention two concurrent
        // returns can both see a sub-cap value and both proceed to
        // cache. The overshoot is bounded by the size of one bucket
        // entry per concurrent return, which is well below the
        // overall budget headroom this cap is protecting.
        if (_maxCachedBytes != long.MaxValue &&
            Volatile.Read(ref _totalBytesCached) + bucketSize > _maxCachedBytes)
        {
            Interlocked.Increment(ref _totalReturnsDroppedForCap);
            return;
        }

        // Capacity guard: increment only if we are below the
        // per-bucket cap. Compare-and-swap loop avoids serializing
        // the bucket and matches the unsynchronized ConcurrentBag
        // pattern.
        while (true)
        {
            var current = Volatile.Read(ref _bucketCounts[bucketIndex]);
            if (current >= PerBucketCap)
            {
                // At cap; drop on the floor.
                return;
            }
            if (Interlocked.CompareExchange(ref _bucketCounts[bucketIndex], current + 1, current) == current)
                break;
        }

        // Defensive zero-out so a buffer that gets recycled cannot
        // leak previous chunk plaintext into a future chunk's
        // pre-fill window. Cheap relative to the bucket sizes the
        // pool handles.
        Array.Clear(buffer);

        _buckets[bucketIndex].Add(buffer);
        var newTotal = Interlocked.Add(ref _totalBytesCached, _bucketSizes[bucketIndex]);
        // B72: charge the retention attribution AFTER the buffer is
        // visible in the bucket so a concurrent Rent that races us can
        // either see+take the cached buffer (and apply the matching
        // release in Rent) or miss it and allocate fresh; either way
        // the steady-state charge equals TotalBytesCached.
        ChargeRetention(bucketSize);

        // CAS-max update of the peak. The loop terminates on the
        // first iteration in the uncontended case and is bounded by
        // the number of concurrent Returns that observed a smaller
        // peak.
        long oldPeak;
        do
        {
            oldPeak = Volatile.Read(ref _peakBytesCached);
            if (newTotal <= oldPeak) break;
        }
        while (Interlocked.CompareExchange(ref _peakBytesCached, newTotal, oldPeak) != oldPeak);

        Interlocked.Increment(ref _totalReturnsAccepted);
    }

    /// <summary>
    /// B72: attribute <paramref name="bytes"/> of pool-cache retention
    /// to the configured <see cref="MemoryBudget"/>. No-op when no
    /// budget was supplied or after the pool was disposed. Never
    /// blocks and never throws; the call site is on the Return hot
    /// path so a blocking or throwing implementation would break the
    /// consumer's upload/restore flow.
    /// </summary>
    private void ChargeRetention(long bytes)
    {
        if (_retentionBudget is null) return;
        _retentionBudget.ChargeRetention(bytes);
        Interlocked.Add(ref _budgetChargedBytes, bytes);
    }

    /// <summary>
    /// B72: release <paramref name="bytes"/> of pool-cache retention
    /// from the configured <see cref="MemoryBudget"/>. Called when a
    /// cache-served Rent or a Dispose drains buffers out of the pool.
    /// The retention counter is decremented to mirror the budget; both
    /// floor at zero so a transient race cannot drive either negative.
    /// </summary>
    private void ReleaseRetentionCharge(long bytes)
    {
        if (_retentionBudget is null) return;
        // Clamp at the currently-charged amount so a race that lands
        // Returns and Rents out of order can never over-release the
        // budget. The budget itself clamps at zero too, but mirroring
        // it here keeps _budgetChargedBytes monotonically consistent
        // with TotalBytesCached.
        long previous;
        long released;
        do
        {
            previous = Volatile.Read(ref _budgetChargedBytes);
            released = Math.Min(previous, bytes);
            if (released == 0) return;
        }
        while (Interlocked.CompareExchange(ref _budgetChargedBytes, previous - released, previous) != previous);
        _retentionBudget.ReleaseRetention(released);
    }

    /// <summary>
    /// Bucket index for a rent of <paramref name="minimumLength"/>
    /// bytes, or -1 when the request falls outside the pool's bucket
    /// range. Returns the SMALLEST bucket whose size is greater than
    /// or equal to <paramref name="minimumLength"/>.
    /// </summary>
    private int GetBucketIndex(int minimumLength)
    {
        for (int i = 0; i < _bucketSizes.Length; i++)
        {
            if (minimumLength <= _bucketSizes[i])
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Bucket index for a return of an exact-size buffer, or -1 when
    /// the size does not match any bucket. Used to reject Return
    /// calls for buffers that did not originate from this pool's
    /// shape.
    /// </summary>
    private int GetBucketIndexForExactSize(int length)
    {
        for (int i = 0; i < _bucketSizes.Length; i++)
        {
            if (length == _bucketSizes[i])
                return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Drain every bucket so the cached buffers become unreachable
        // and the GC can reclaim them on the next collection. We do
        // not zero-out here -- Return already zeroed every accepted
        // buffer when it landed in the bucket.
        for (int i = 0; i < _buckets.Length; i++)
        {
            while (_buckets[i].TryTake(out _))
            {
                // discard
            }
            Volatile.Write(ref _bucketCounts[i], 0);
        }
        Volatile.Write(ref _totalBytesCached, 0);

        // B72: drain every byte we charged against the retention budget
        // so disposing the pool leaves the budget with no lingering
        // attribution. Producers that are still waiting on AcquireAsync
        // (only possible during a hard cancellation tear-down) will see
        // the headroom open up immediately. Snapshot-then-CAS so a
        // concurrent ReleaseRetentionCharge cannot double-release.
        if (_retentionBudget is not null)
        {
            long charged = Interlocked.Exchange(ref _budgetChargedBytes, 0);
            if (charged > 0)
                _retentionBudget.ReleaseRetention(charged);
        }
    }
}
