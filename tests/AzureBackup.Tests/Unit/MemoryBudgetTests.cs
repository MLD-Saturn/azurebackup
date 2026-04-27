using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for <see cref="MemoryBudget"/> covering basic acquire/release,
/// the at-least-one guarantee, unlimited budget fast path, concurrent access,
/// and <see cref="MemoryBudget.FromConfig"/> factory.
/// </summary>
public class MemoryBudgetTests : IDisposable
{
    private MemoryBudget? _budget;

    public void Dispose()
    {
        _budget?.Dispose();
    }

    [Fact]
    public async Task AcquireReleaseSingleWithinBudgetSucceeds()
    {
        _budget = new MemoryBudget(1000);

        await _budget.AcquireAsync(500);

        Assert.Equal(500, _budget.UsedBytes);
        Assert.Equal(500, _budget.RemainingBytes);

        _budget.Release(500);

        Assert.Equal(0, _budget.UsedBytes);
        Assert.Equal(1000, _budget.RemainingBytes);
    }

    [Fact]
    public async Task MultipleAcquiresWithinBudgetSucceed()
    {
        _budget = new MemoryBudget(1000);

        await _budget.AcquireAsync(300);
        await _budget.AcquireAsync(300);
        await _budget.AcquireAsync(300);

        Assert.Equal(900, _budget.UsedBytes);

        _budget.Release(300);
        _budget.Release(300);
        _budget.Release(300);

        Assert.Equal(0, _budget.UsedBytes);
    }

    [Fact]
    public async Task AtLeastOneGuaranteeAllowsOversizedAcquireWhenEmpty()
    {
        _budget = new MemoryBudget(100);

        // Single acquire that exceeds total budget — should succeed because nothing is in-flight
        await _budget.AcquireAsync(500);

        Assert.Equal(500, _budget.UsedBytes);

        _budget.Release(500);
        Assert.Equal(0, _budget.UsedBytes);
    }

    [Fact]
    public async Task AcquireBlocksWhenBudgetFullThenSucceedsAfterRelease()
    {
        _budget = new MemoryBudget(1000);

        // Fill the budget
        await _budget.AcquireAsync(800);

        // Start a second acquire that doesn't fit — it should block
        var acquired = false;
        var acquireTask = Task.Run(async () =>
        {
            await _budget.AcquireAsync(500);
            acquired = true;
        });

        // Give the task time to start waiting
        await Task.Delay(100);
        Assert.False(acquired);

        // Release enough to fit the pending request
        _budget.Release(800);

        // The blocked acquire should now complete
        await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(acquired);
        Assert.Equal(500, _budget.UsedBytes);

        _budget.Release(500);
    }

    [Fact]
    public async Task AcquireCancellationThrowsOperationCanceled()
    {
        _budget = new MemoryBudget(100);

        // Fill budget so next acquire blocks
        await _budget.AcquireAsync(100);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _budget.AcquireAsync(50, cts.Token));

        _budget.Release(100);
    }

    [Fact]
    public async Task UnlimitedBudgetAcquireAlwaysSucceedsImmediately()
    {
        _budget = new MemoryBudget(long.MaxValue);

        Assert.True(_budget.IsUnlimited);

        // Even huge acquires succeed without tracking
        await _budget.AcquireAsync(long.MaxValue / 2);
        await _budget.AcquireAsync(long.MaxValue / 2);

        // UsedBytes stays 0 for unlimited (no tracking overhead)
        Assert.Equal(0, _budget.UsedBytes);

        _budget.Release(1000);
        Assert.Equal(0, _budget.UsedBytes);
    }

    [Fact]
    public async Task ConcurrentAcquiresRespectBudget()
    {
        _budget = new MemoryBudget(1000);
        var maxObserved = 0L;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await _budget.AcquireAsync(200);
            try
            {
                lock (lockObj)
                {
                    maxObserved = Math.Max(maxObserved, _budget.UsedBytes);
                }

                await Task.Delay(10); // simulate work
            }
            finally
            {
                _budget.Release(200);
            }
        });

        await Task.WhenAll(tasks);

        // With 200 per slot and 1000 budget, at most 5 should be in-flight
        Assert.True(maxObserved <= 1000, $"Peak usage {maxObserved} exceeded budget 1000");
        Assert.Equal(0, _budget.UsedBytes);
    }

    [Fact]
    public void FromConfigDisabledReturnsUnlimited()
    {
        var config = new BackupConfiguration
        {
            MemoryLimitEnabled = false,
            MemoryLimitMB = 512
        };

        using var budget = MemoryBudget.FromConfig(config);

        Assert.True(budget.IsUnlimited);
        Assert.Equal(long.MaxValue, budget.TotalBytes);
    }

    [Fact]
    public void FromConfigEnabledReturnsCorrectBudget()
    {
        var config = new BackupConfiguration
        {
            MemoryLimitEnabled = true,
            MemoryLimitMB = 512
        };

        using var budget = MemoryBudget.FromConfig(config);

        Assert.False(budget.IsUnlimited);
        Assert.Equal(512L * 1024 * 1024, budget.TotalBytes);
    }

    [Fact]
    public void FromConfigSubtractsFixedOverhead()
    {
        var config = new BackupConfiguration
        {
            MemoryLimitEnabled = true,
            MemoryLimitMB = 512
        };
        var overhead = 128L * 1024 * 1024; // 128 MB CDC buffer

        using var budget = MemoryBudget.FromConfig(config, overhead);

        Assert.Equal(512L * 1024 * 1024 - overhead, budget.TotalBytes);
    }

    [Fact]
    public void FromConfigOverheadExceedsBudgetClampToOne()
    {
        var config = new BackupConfiguration
        {
            MemoryLimitEnabled = true,
            MemoryLimitMB = 100
        };
        var overhead = 200L * 1024 * 1024; // overhead exceeds budget

        using var budget = MemoryBudget.FromConfig(config, overhead);

        Assert.Equal(1, budget.TotalBytes);
    }

    [Fact]
    public void ConstructorRejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBudget(0));
    }

    [Fact]
    public void ConstructorRejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBudget(-1));
    }

    [Fact]
    public async Task AcquireRejectsZeroBytes()
    {
        _budget = new MemoryBudget(1000);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _budget.AcquireAsync(0));
    }

    [Fact]
    public void ReleaseRejectsZeroBytes()
    {
        _budget = new MemoryBudget(1000);

        Assert.Throws<ArgumentOutOfRangeException>(() => _budget.Release(0));
    }

    [Fact]
    public void ReleaseNeverGoesNegative()
    {
        _budget = new MemoryBudget(1000);

        _budget.Release(500);

        Assert.Equal(0, _budget.UsedBytes);
    }

    [Fact]
    public void TotalBytesReflectsConstructorValue()
    {
        _budget = new MemoryBudget(42);

        Assert.Equal(42, _budget.TotalBytes);
        Assert.False(_budget.IsUnlimited);
    }

    // ---------------------------------------------------------------------
    // B34: tightened at-least-one branch
    //
    // Pre-B34 behaviour: any request that fit OR found _usedBytes==0 was
    // admitted. Post-B34: the deadlock-avoidance branch admits oversized
    // requests ONLY when bytes > totalBytes AND _usedBytes == 0. A
    // request that COULD fit by waiting must wait, even when the budget
    // is momentarily empty. Each oversized admission increments
    // OversizedAdmissions so the breach is observable.
    // ---------------------------------------------------------------------

    [Fact]
    public void OversizedAdmissionsStartsAtZero()
    {
        _budget = new MemoryBudget(1000);

        Assert.Equal(0, _budget.OversizedAdmissions);
    }

    [Fact]
    public async Task AcquireBytesGreaterThanTotalIncrementsOversizedAdmissions()
    {
        _budget = new MemoryBudget(100);

        // Single acquire that exceeds the entire budget. Pre-B34 this took
        // the at-least-one branch silently; post-B34 it still succeeds
        // (deadlock-avoidance is preserved) but the counter is incremented.
        await _budget.AcquireAsync(500);

        Assert.Equal(500, _budget.UsedBytes);
        Assert.Equal(1, _budget.OversizedAdmissions);

        _budget.Release(500);
    }

    [Fact]
    public async Task AcquireBytesEqualToTotalDoesNotCountAsOversized()
    {
        _budget = new MemoryBudget(100);

        // bytes == totalBytes fits within the cap (the <= test in the
        // fast path), so this is NOT an oversized admission.
        await _budget.AcquireAsync(100);

        Assert.Equal(100, _budget.UsedBytes);
        Assert.Equal(0, _budget.OversizedAdmissions);

        _budget.Release(100);
    }

    [Fact]
    public async Task AcquireBlocksWhenWouldFitButBudgetMomentarilyDrained()
    {
        // B34 regression guard: pre-B34 a fitting request would have
        // taken the at-least-one branch as soon as the budget read 0,
        // even though it could fit by waiting. With 16-way file
        // parallelism this caused the budget to admit unbounded
        // concurrent requests during transient drain windows. Post-B34
        // such a request must wait for the slow path so the budget cap
        // actually holds across all 16 callers.
        _budget = new MemoryBudget(1000);

        // Acquire and immediately release so _usedBytes returns to 0 but
        // the budget has clearly been used.
        await _budget.AcquireAsync(800);
        _budget.Release(800);

        // Now fill the budget with one call.
        await _budget.AcquireAsync(800);

        // A second 500-byte request fits (500 <= 1000) but does not fit
        // RIGHT NOW (500 + 800 > 1000). It must block, not take an
        // at-least-one shortcut.
        var secondAcquired = false;
        var secondTask = Task.Run(async () =>
        {
            await _budget.AcquireAsync(500);
            secondAcquired = true;
        });

        await Task.Delay(150);
        Assert.False(secondAcquired);
        Assert.Equal(0, _budget.OversizedAdmissions);

        _budget.Release(800);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondAcquired);
        Assert.Equal(0, _budget.OversizedAdmissions);

        _budget.Release(500);
    }

    [Fact]
    public async Task AtLeastOneGuaranteeStillFiresForGenuinelyOversizedRequests()
    {
        // Companion to the test above: confirm we did not break the
        // legitimate deadlock-avoidance case. A single request that
        // CANNOT fit (bytes > totalBytes) must still be admitted when
        // the budget is empty, otherwise the system deadlocks on a
        // chunk that legitimately exceeds the configured cap.
        _budget = new MemoryBudget(100);

        await _budget.AcquireAsync(500);

        Assert.Equal(500, _budget.UsedBytes);
        Assert.Equal(1, _budget.OversizedAdmissions);

        _budget.Release(500);
    }

    [Fact]
    public async Task UnlimitedBudgetDoesNotIncrementOversizedAdmissions()
    {
        _budget = new MemoryBudget(long.MaxValue);

        await _budget.AcquireAsync(long.MaxValue / 2);

        // Unlimited fast-paths immediately and never reaches the
        // oversized-admission code path.
        Assert.Equal(0, _budget.OversizedAdmissions);
    }
}
