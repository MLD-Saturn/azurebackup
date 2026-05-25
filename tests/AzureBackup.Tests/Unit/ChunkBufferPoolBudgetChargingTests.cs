using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B72 (W5 Phase 4): targeted tests for the pool-retention budget
/// charging that <see cref="ChunkBufferPool"/> applies when its
/// optional third constructor argument supplies a
/// <see cref="MemoryBudget"/>. These tests are deliberately separate
/// from the parameterized <see cref="ChunkBufferPoolTests"/> suite
/// because the budget seam is new in B72 and we want a single test
/// class to focus on the charge / release / drain invariants without
/// fanning out across both bucket geometries.
/// </summary>
public sealed class ChunkBufferPoolBudgetChargingTests
{
    private const int MB = 1024 * 1024;

    private static int[] SmallBuckets => ChunkBufferPool.SmallChunkBucketSizes;

    [Fact]
    public void Constructor_WithoutBudget_BudgetChargedBytesIsZero()
    {
        using var pool = new ChunkBufferPool(SmallBuckets);

        Assert.Equal(0, pool.BudgetChargedBytes);
    }

    [Fact]
    public void Return_AcceptedIntoCache_ChargesRetentionAgainstBudget()
    {
        using var budget = new MemoryBudget(128L * MB);
        using var pool = new ChunkBufferPool(SmallBuckets, long.MaxValue, budget);

        var (buffer, fromPool) = pool.Rent(64 * 1024);
        Assert.False(fromPool);
        Assert.Equal(0, pool.BudgetChargedBytes);
        Assert.Equal(0, budget.UsedBytes);

        pool.Return(buffer);

        Assert.Equal(64 * 1024L, pool.BudgetChargedBytes);
        Assert.Equal(64 * 1024L, budget.UsedBytes);
        Assert.Equal(64 * 1024L, budget.PeakUsedBytes);
    }

    [Fact]
    public void RentFromCache_ReleasesRetentionFromBudget()
    {
        using var budget = new MemoryBudget(128L * MB);
        using var pool = new ChunkBufferPool(SmallBuckets, long.MaxValue, budget);

        var (first, _) = pool.Rent(64 * 1024);
        pool.Return(first);
        Assert.Equal(64 * 1024L, budget.UsedBytes);

        var (second, fromPool) = pool.Rent(64 * 1024);

        Assert.True(fromPool);
        Assert.Same(first, second);
        Assert.Equal(0, pool.BudgetChargedBytes);
        Assert.Equal(0, budget.UsedBytes);
        // Peak is sticky -- the peak measured during the cached
        // interval must remain visible after the cache drains.
        Assert.Equal(64 * 1024L, budget.PeakUsedBytes);
    }

    [Fact]
    public void Return_DroppedForCap_DoesNotChargeBudget()
    {
        using var budget = new MemoryBudget(128L * MB);
        // Tiny global cap so the SECOND return is forced to drop.
        using var pool = new ChunkBufferPool(SmallBuckets, maxCachedBytes: 64 * 1024, retentionBudget: budget);

        var (a, _) = pool.Rent(64 * 1024);
        var (b, _) = pool.Rent(64 * 1024);

        pool.Return(a);
        Assert.Equal(64 * 1024L, budget.UsedBytes);

        pool.Return(b); // would push cached bytes above the 64 KB cap; dropped

        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
        Assert.Equal(64 * 1024L, budget.UsedBytes);
        Assert.Equal(64 * 1024L, pool.BudgetChargedBytes);
    }

    [Fact]
    public void Dispose_DrainsBudgetRetention()
    {
        using var budget = new MemoryBudget(128L * MB);
        var pool = new ChunkBufferPool(SmallBuckets, long.MaxValue, budget);

        var (a, _) = pool.Rent(64 * 1024);
        var (b, _) = pool.Rent(256 * 1024);
        pool.Return(a);
        pool.Return(b);

        Assert.Equal((64 + 256) * 1024L, budget.UsedBytes);

        pool.Dispose();

        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, pool.BudgetChargedBytes);
        // Peak survives Dispose so the operator can see the high-water
        // mark that the pool reached before it was torn down.
        Assert.Equal((64 + 256) * 1024L, budget.PeakUsedBytes);
    }

    [Fact]
    public void Constructor_NullBudget_BehavesIdenticallyToTwoArgOverload()
    {
        // The three-arg constructor with null is documented as the
        // pre-B72 behaviour; this test pins that contract so a future
        // refactor cannot silently change it.
        using var pool = new ChunkBufferPool(SmallBuckets, long.MaxValue, retentionBudget: null);

        var (a, _) = pool.Rent(64 * 1024);
        pool.Return(a);

        Assert.Equal(0, pool.BudgetChargedBytes);
        Assert.Equal(64 * 1024L, pool.TotalBytesCached);
    }

    [Fact]
    public void UnlimitedBudget_RetentionChargeIsNoOp()
    {
        using var budget = new MemoryBudget(long.MaxValue);
        using var pool = new ChunkBufferPool(SmallBuckets, long.MaxValue, budget);

        var (a, _) = pool.Rent(64 * 1024);
        pool.Return(a);

        // Pool's own counter still records the attribution attempt so
        // the Rent-from-cache release stays symmetric; the budget
        // ignores it because IsUnlimited short-circuits both methods.
        Assert.Equal(64 * 1024L, pool.BudgetChargedBytes);
        Assert.Equal(0, budget.UsedBytes);
        Assert.True(budget.IsUnlimited);
    }
}
