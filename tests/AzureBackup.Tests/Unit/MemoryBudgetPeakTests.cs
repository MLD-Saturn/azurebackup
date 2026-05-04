using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B56 (W3 Phase F): tests for the new <see cref="MemoryBudget.PeakUsedBytes"/>
/// high-water mark surfaced through the <c>[mem]</c> log line. The peak
/// must rise monotonically with every acquire that grows the live used
/// total, and must NOT decrease when bytes are released, so a post-hoc
/// log reading shows whether the operation actually saturated the
/// configured ceiling.
/// </summary>
public class MemoryBudgetPeakTests
{
    [Fact]
    public void NewBudget_PeakIsZero()
    {
        using var budget = new MemoryBudget(1024);

        Assert.Equal(0, budget.PeakUsedBytes);
    }

    [Fact]
    public async Task SingleAcquire_PeakMatchesUsed()
    {
        using var budget = new MemoryBudget(1024);

        await budget.AcquireAsync(256);

        Assert.Equal(256, budget.UsedBytes);
        Assert.Equal(256, budget.PeakUsedBytes);
    }

    [Fact]
    public async Task ReleaseBelowPeak_PeakStays()
    {
        using var budget = new MemoryBudget(1024);
        await budget.AcquireAsync(512);
        await budget.AcquireAsync(256);

        Assert.Equal(768, budget.UsedBytes);
        Assert.Equal(768, budget.PeakUsedBytes);

        budget.Release(512);

        Assert.Equal(256, budget.UsedBytes);
        Assert.Equal(768, budget.PeakUsedBytes);
    }

    [Fact]
    public async Task GrowAfterRelease_PeakAdvances()
    {
        using var budget = new MemoryBudget(1024);
        await budget.AcquireAsync(512);
        budget.Release(512);

        await budget.AcquireAsync(900);

        Assert.Equal(900, budget.UsedBytes);
        Assert.Equal(900, budget.PeakUsedBytes);
    }

    [Fact]
    public async Task UnlimitedBudget_PeakStaysZero()
    {
        // Unlimited path bypasses the lock entirely; peak is meaningless
        // there because there is no cap to saturate against.
        using var budget = new MemoryBudget(long.MaxValue);

        await budget.AcquireAsync(1024L * 1024 * 1024);

        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, budget.PeakUsedBytes);
    }

    [Fact]
    public async Task OversizedAdmission_StillCountsTowardPeak()
    {
        // The B34 deadlock-avoidance branch admits a single oversized
        // request when the budget is empty; that admission must still
        // contribute to the peak so the breach is visible alongside
        // OversizedAdmissions.
        using var budget = new MemoryBudget(totalBytes: 100);

        await budget.AcquireAsync(500);

        Assert.Equal(500, budget.UsedBytes);
        Assert.Equal(500, budget.PeakUsedBytes);
        Assert.Equal(1, budget.OversizedAdmissions);
    }

    [Fact]
    public async Task MultipleAcquires_PeakIsHighWaterMark()
    {
        using var budget = new MemoryBudget(1024);
        await budget.AcquireAsync(100);
        await budget.AcquireAsync(200);
        await budget.AcquireAsync(300);

        Assert.Equal(600, budget.PeakUsedBytes);

        budget.Release(300);
        await budget.AcquireAsync(50);

        // Peak should still be 600, not the new 350 total.
        Assert.Equal(600, budget.PeakUsedBytes);
    }
}
