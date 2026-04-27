using System.Collections.Concurrent;

namespace AzureBackup.Core.Services;

/// <summary>
/// B37: bounded recycler for the very-large LOH-resident <c>byte[]</c>
/// allocations that <see cref="ChunkingService"/> uses for chunks at or
/// above <see cref="ChunkingService.PoolSkipThresholdBytes"/>.
///
/// <para>
/// Motivation: B33 (in commit 0f11c59) swapped
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> rentals for exact
/// <c>new byte[]</c> allocations on the large-chunk path because the
/// shared pool's per-core caches retain large arrays indefinitely and
/// silently undercount the budget. The exact-allocation path closed
/// that gap but introduced a different one: every chunk's payload
/// buffer is now an LOH-resident <c>byte[]</c> that the GC can only
/// reclaim during a gen-2 collection. Under the production-test
/// workload (many concurrent 16-128 MB chunks), gen-2 collections fire
/// roughly once a minute, and ~6 GB of dead-but-unreclaimed LOH
/// accumulates between collections. On a 32 GB host with a 16 GB
/// budget that residency lives entirely outside the budget's accounting
/// and pushes <c>privateMem</c> well past physical RAM.
/// </para>
///
/// <para>
/// Solution shape: a dedicated, strictly-bounded pool that caches LOH
/// buffers across chunks. Rented buffers are ALIVE forever (the pool
/// retains them) so the GC never needs to reclaim them, eliminating the
/// gen-2 retention problem. The cap enforces the residency ceiling:
/// when the pool is at capacity the next rent allocates a fresh
/// <c>byte[]</c> that the GC reclaims on gen-2 (the pre-B37 behaviour
/// for the overflow case), and when a buffer is returned beyond the
/// per-bucket cap it is dropped on the floor (let the GC reclaim it).
/// The pool is a steady-state recycler, not an unbounded cache.
/// </para>
///
/// <para>
/// Bucketing: requests are rounded up to the next power-of-two and
/// indexed into a fixed set of buckets (16 MB, 32 MB, 64 MB, 128 MB,
/// 256 MB). Sizes outside that range allocate fresh and do not flow
/// through the pool -- the smallest covers chunks at the
/// <see cref="ChunkingService.PoolSkipThresholdBytes"/> threshold, the
/// largest covers the worst-case ArrayPool tier ceiling for a 128 MB
/// chunk's encrypt buffer.
/// </para>
///
/// <para>
/// Concurrency: each bucket is a <see cref="ConcurrentBag{T}"/> guarded
/// by an <see cref="Interlocked"/>-managed count. Under contention a
/// bucket's count can briefly exceed the cap (the count is decremented
/// AFTER bag.TryTake; another thread can rent first), but the cap is
/// only an advisory ceiling -- a small overshoot has no correctness
/// implication, only a short-term residency overshoot. The
/// alternative of a strict lock-protected count would serialize all
/// rents and starve the producers.
/// </para>
///
/// <para>
/// Interaction with <see cref="MemoryBudget"/>: the pool does NOT
/// itself charge the budget. The producer in
/// <see cref="ChunkingService"/> charges as before; the pool is a pure
/// allocation cache underneath. The budget remains the single throttle.
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
public sealed class LargeChunkBufferPool : IDisposable
{
    private const int MB = 1024 * 1024;

    /// <summary>
    /// Bucket sizes, smallest first. Power-of-two-spaced so the
    /// rounding-up logic in <see cref="GetBucketIndex"/> is a single
    /// branch-free computation. Range covers
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> (16 MB) up
    /// to the worst-case encrypt-buffer tier ceiling for a 128 MB
    /// chunk (256 MB).
    /// </summary>
    internal static readonly int[] BucketSizes =
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
    /// Sizing rationale: with B27's 16-way file concurrency × 6-way
    /// chunk concurrency = 96 in-flight chunks worst case, and most
    /// chunks landing in the 64 MB bucket on the
    /// <see cref="ChunkingService"/> default config, a per-bucket cap
    /// of 32 covers ~one third of the worst-case in-flight count
    /// without unbounded growth. The bucket caps multiply against
    /// <see cref="BucketSizes"/> to give the worst-case pool
    /// residency: 32 × (16 + 32 + 64 + 128 + 256) MB = 32 × 496 MB
    /// = 15.5 GB. That is the absolute residency ceiling the pool
    /// can imprint on the heap, and it lives entirely within a
    /// reasonable 16 GB+ budget.
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

    private readonly ConcurrentBag<byte[]>[] _buckets;
    private readonly int[] _bucketCounts;
    private long _totalBytesCached;
    private long _totalRents;
    private long _totalRentsFromPool;
    private long _totalReturns;
    private long _totalReturnsAccepted;
    private int _disposed;

    /// <summary>
    /// Creates a new, empty pool. Buckets fill on demand through
    /// <see cref="Rent"/>/<see cref="Return"/>.
    /// </summary>
    public LargeChunkBufferPool()
    {
        _buckets = new ConcurrentBag<byte[]>[BucketSizes.Length];
        _bucketCounts = new int[BucketSizes.Length];
        for (int i = 0; i < BucketSizes.Length; i++)
            _buckets[i] = new ConcurrentBag<byte[]>();
    }

    /// <summary>
    /// Total bytes currently cached across all buckets. Snapshot only;
    /// useful for diagnostics and the B36 memory-log emitter.
    /// </summary>
    public long TotalBytesCached => Volatile.Read(ref _totalBytesCached);

    /// <summary>Number of rent calls (pool-served + fresh-allocation).</summary>
    public long TotalRents => Volatile.Read(ref _totalRents);

    /// <summary>Number of rent calls served from the pool's cached buffers.</summary>
    public long TotalRentsFromPool => Volatile.Read(ref _totalRentsFromPool);

    /// <summary>Number of return calls.</summary>
    public long TotalReturns => Volatile.Read(ref _totalReturns);

    /// <summary>Number of return calls that were actually cached (vs dropped on the floor).</summary>
    public long TotalReturnsAccepted => Volatile.Read(ref _totalReturnsAccepted);

    /// <summary>
    /// Pool hit rate as a fraction in [0, 1]. Returns 0 when no rents
    /// have happened yet. A hit rate near 1 means the pool is doing
    /// its job; near 0 means most rents are still allocating fresh and
    /// the pool is providing little residency benefit.
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
    /// fresh <c>byte[]</c> was allocated (which the caller MUST still
    /// pass back to <see cref="Return"/> so the pool can decide
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

        var bucketSize = BucketSizes[bucketIndex];
        if (_buckets[bucketIndex].TryTake(out var cached))
        {
            // Decrement BEFORE marking the rent as pool-served so the
            // count never sits below zero from a transient ordering.
            Interlocked.Decrement(ref _bucketCounts[bucketIndex]);
            Interlocked.Add(ref _totalBytesCached, -bucketSize);
            Interlocked.Increment(ref _totalRentsFromPool);
            return (cached, true);
        }

        // Bucket is empty -- allocate fresh at the bucket's full size
        // (not minimumLength) so the eventual Return call can match a
        // bucket. Returning a smaller-than-bucket array would force a
        // bucket-size mismatch on Return, leaking the buffer.
        return (new byte[bucketSize], false);
    }

    /// <summary>
    /// Returns a buffer to the pool. The buffer is cached IF its
    /// length matches a bucket size AND the bucket is below its cap;
    /// otherwise the buffer is dropped on the floor (the GC will
    /// reclaim it). The latter is the back-pressure mechanism that
    /// keeps the pool's total residency bounded.
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

        // Capacity guard: increment only if we are below the cap.
        // Compare-and-swap loop avoids serializing the bucket and
        // matches the unsynchronized ConcurrentBag pattern.
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
        // pre-fill window. Cheap relative to the chunk size.
        Array.Clear(buffer);

        _buckets[bucketIndex].Add(buffer);
        Interlocked.Add(ref _totalBytesCached, BucketSizes[bucketIndex]);
        Interlocked.Increment(ref _totalReturnsAccepted);
    }

    /// <summary>
    /// Bucket index for a rent of <paramref name="minimumLength"/>
    /// bytes, or -1 when the request falls outside the pool's bucket
    /// range. Returns the SMALLEST bucket whose size is >=
    /// <paramref name="minimumLength"/>.
    /// </summary>
    private static int GetBucketIndex(int minimumLength)
    {
        for (int i = 0; i < BucketSizes.Length; i++)
        {
            if (minimumLength <= BucketSizes[i])
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
    private static int GetBucketIndexForExactSize(int length)
    {
        for (int i = 0; i < BucketSizes.Length; i++)
        {
            if (length == BucketSizes[i])
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
    }
}
