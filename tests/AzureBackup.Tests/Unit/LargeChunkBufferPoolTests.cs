using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B37: tests for <see cref="LargeChunkBufferPool"/>. The pool is the
/// LOH-recycler that <see cref="ChunkingService"/> uses for chunks at
/// or above <see cref="ChunkingService.PoolSkipThresholdBytes"/>;
/// these tests focus on the pool itself in isolation. The integration
/// with the chunking service is exercised separately via the existing
/// orchestrator tests.
/// </summary>
public class LargeChunkBufferPoolTests
{
    private const int MB = 1024 * 1024;

    [Fact]
    public void RentEmpty_AllocatesFreshAtBucketSize()
    {
        using var pool = new LargeChunkBufferPool();

        var (buffer, fromPool) = pool.Rent(20 * MB);

        Assert.False(fromPool);
        Assert.Equal(32 * MB, buffer.Length); // rounded up to next bucket
    }

    [Fact]
    public void RentReturnRent_ServesFromPool()
    {
        using var pool = new LargeChunkBufferPool();

        var (first, _) = pool.Rent(20 * MB);
        Assert.False(pool.HitRate > 0); // first rent allocates fresh

        pool.Return(first);

        var (second, fromPool) = pool.Rent(20 * MB);
        Assert.True(fromPool);
        Assert.Same(first, second);
    }

    [Fact]
    public void RentExactBucketSize_RoundsToSelf()
    {
        using var pool = new LargeChunkBufferPool();

        var (buffer, _) = pool.Rent(64 * MB);
        Assert.Equal(64 * MB, buffer.Length);
    }

    [Fact]
    public void RentBelowSmallestBucket_AllocatesFreshAtBucketSize()
    {
        // The smallest bucket is 16 MB. A rent of 1 MB rounds up.
        using var pool = new LargeChunkBufferPool();

        var (buffer, _) = pool.Rent(1 * MB);

        Assert.Equal(16 * MB, buffer.Length);
    }

    [Fact]
    public void RentAboveLargestBucket_AllocatesFreshAtRequestedSize()
    {
        // 256 MB is the largest bucket. A rent of 300 MB falls
        // outside the pool entirely and returns an exact-size array.
        using var pool = new LargeChunkBufferPool();

        var (buffer, fromPool) = pool.Rent(300 * MB);

        Assert.False(fromPool);
        Assert.Equal(300 * MB, buffer.Length);
    }

    [Fact]
    public void ReturnArrayOutsideBucketSizes_DropsSilently()
    {
        // A return of an exact-300-MB array does not match any
        // bucket; the pool drops it on the floor without throwing.
        using var pool = new LargeChunkBufferPool();
        var oversized = new byte[300 * MB];

        pool.Return(oversized);

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(0, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturns);
    }

    [Fact]
    public void ReturnAtCap_DropsSilently()
    {
        // Per-bucket cap is 32. Return 33 buffers of the same
        // bucket size; the 33rd must be dropped.
        using var pool = new LargeChunkBufferPool();
        var buffers = new byte[33][];
        for (int i = 0; i < 33; i++)
            buffers[i] = new byte[16 * MB];

        for (int i = 0; i < 33; i++)
            pool.Return(buffers[i]);

        // 32 accepted, 1 dropped.
        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(32L * 16 * MB, pool.TotalBytesCached);
    }

    [Fact]
    public void ReturnZeroesBuffer_PreventsCrossChunkLeak()
    {
        // Return must zero the buffer so a recycled rent cannot read
        // stale plaintext from a previous chunk that briefly
        // pre-filled the buffer before being overwritten.
        using var pool = new LargeChunkBufferPool();
        var (buffer, _) = pool.Rent(16 * MB);

        // Stamp a recognizable pattern.
        for (int i = 0; i < 256; i++)
            buffer[i] = 0xCD;

        pool.Return(buffer);

        var (again, fromPool) = pool.Rent(16 * MB);
        Assert.True(fromPool);
        Assert.Same(buffer, again);

        // Every byte must read 0 after Return -- the recycle path is
        // the only way a 0xCD byte could reach this point.
        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)0, again[i]);
    }

    [Fact]
    public void HitRate_ReflectsPoolEffectiveness()
    {
        using var pool = new LargeChunkBufferPool();

        // Cycle 10 buffers through the pool: 10 fresh allocations,
        // 10 returns, then 10 more rents (all served from pool).
        var buffers = new byte[10][];
        for (int i = 0; i < 10; i++)
            buffers[i] = pool.Rent(16 * MB).Buffer;
        for (int i = 0; i < 10; i++)
            pool.Return(buffers[i]);
        for (int i = 0; i < 10; i++)
            pool.Rent(16 * MB);

        // 20 total rents, 10 served from the pool.
        Assert.Equal(20, pool.TotalRents);
        Assert.Equal(10, pool.TotalRentsFromPool);
        Assert.Equal(0.5, pool.HitRate, precision: 2);
    }

    [Fact]
    public void Dispose_ClearsBucketsAndStopsCaching()
    {
        var pool = new LargeChunkBufferPool();
        pool.Return(new byte[16 * MB]);
        Assert.Equal(16L * MB, pool.TotalBytesCached);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pool = new LargeChunkBufferPool();
        pool.Dispose();
        pool.Dispose();
        // No exception.
    }

    [Fact]
    public void RentAfterDispose_Throws()
    {
        var pool = new LargeChunkBufferPool();
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(16 * MB));
    }

    [Fact]
    public void ReturnAfterDispose_DropsSilently()
    {
        // Shutdown-race tolerance: a Return call that arrives after
        // Dispose must NOT throw. Producers may call Return on a
        // pool that the orchestrator has already disposed at the
        // end of a backup operation; throwing there would surface
        // an unrelated exception in the cleanup path.
        var pool = new LargeChunkBufferPool();
        pool.Dispose();

        pool.Return(new byte[16 * MB]); // must not throw
    }

    [Fact]
    public void ConcurrentRentReturn_NoCorruptionOrLeak()
    {
        // Stress: 64 threads each cycle 100 rent/return pairs on
        // the smallest bucket. The pool must not throw, must not
        // exceed its cap by more than the natural CAS race window,
        // and must end with TotalBytesCached <= cap × bucketSize.
        using var pool = new LargeChunkBufferPool();
        var threads = new Thread[64];
        var iterationsPerThread = 100;

        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var (buf, _) = pool.Rent(16 * MB);
                    Assert.NotNull(buf);
                    pool.Return(buf);
                }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(64 * iterationsPerThread, pool.TotalRents);
        Assert.Equal(64 * iterationsPerThread, pool.TotalReturns);
        // After all threads finish, no rents are outstanding -- so
        // every acceptable return is in the pool. The cap is 32, so
        // residency must be <= 32 × 16 MB.
        Assert.True(pool.TotalBytesCached <= 32L * 16 * MB,
            $"Residency {pool.TotalBytesCached} exceeded cap {32L * 16 * MB}");
    }

    [Fact]
    public void RentZero_Throws()
    {
        using var pool = new LargeChunkBufferPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
    }

    [Fact]
    public void RentNegative_Throws()
    {
        using var pool = new LargeChunkBufferPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Fact]
    public void ReturnNull_Throws()
    {
        using var pool = new LargeChunkBufferPool();

        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    // ---- B52: global byte-cap behaviour ----

    [Fact]
    public void DefaultConstructor_HasUnlimitedGlobalCap()
    {
        using var pool = new LargeChunkBufferPool();

        Assert.Equal(long.MaxValue, pool.MaxCachedBytes);
    }

    [Fact]
    public void GlobalCap_DropsReturnsThatWouldExceedCap()
    {
        // Cap at 32 MB: the smallest bucket is 16 MB, so two
        // returns at 16 MB each fit, the third must be dropped.
        using var pool = new LargeChunkBufferPool(maxCachedBytes: 32L * MB);

        pool.Return(new byte[16 * MB]); // accepted: cached=16 MB
        pool.Return(new byte[16 * MB]); // accepted: cached=32 MB
        pool.Return(new byte[16 * MB]); // dropped: would push to 48 MB > 32 MB cap

        Assert.Equal(3, pool.TotalReturns);
        Assert.Equal(2, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
        Assert.Equal(32L * MB, pool.TotalBytesCached);
    }

    [Fact]
    public void GlobalCap_AcceptsAgainAfterRentDrainsResidency()
    {
        // Same cap: 32 MB. Fill, then rent one back to drop residency
        // to 16 MB, then a fresh return must be accepted again.
        using var pool = new LargeChunkBufferPool(maxCachedBytes: 32L * MB);
        pool.Return(new byte[16 * MB]);
        pool.Return(new byte[16 * MB]);

        var (rented, fromPool) = pool.Rent(16 * MB);
        Assert.True(fromPool);
        Assert.Equal(16L * MB, pool.TotalBytesCached);

        pool.Return(new byte[16 * MB]); // back to 32 MB cap
        Assert.Equal(32L * MB, pool.TotalBytesCached);
        // Three accepted across the run: two on the initial fill, one
        // after the residency drained back below the cap.
        Assert.Equal(3, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
    }

    [Fact]
    public void GlobalCap_PerBucketCapStillApplies()
    {
        // Generous global cap (1 GB), tiny per-bucket cap defaults
        // to 32. A 33rd 16 MB return must still be dropped per the
        // per-bucket rule, even though the global cap has plenty of
        // headroom. Drops in this case are NOT counted under
        // TotalReturnsDroppedForCap because they are per-bucket
        // rejections, not global-cap rejections.
        using var pool = new LargeChunkBufferPool(maxCachedBytes: 1024L * MB);
        for (int i = 0; i < 33; i++)
            pool.Return(new byte[16 * MB]);

        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
        Assert.Equal(32L * 16 * MB, pool.TotalBytesCached);
    }

    [Fact]
    public void GlobalCapZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LargeChunkBufferPool(maxCachedBytes: 0));
    }

    [Fact]
    public void GlobalCapNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LargeChunkBufferPool(maxCachedBytes: -1));
    }

    [Fact]
    public void Dispose_ResetsResidencyButRetainsCap()
    {
        var pool = new LargeChunkBufferPool(maxCachedBytes: 64L * MB);
        pool.Return(new byte[16 * MB]);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(64L * MB, pool.MaxCachedBytes);
    }
}
