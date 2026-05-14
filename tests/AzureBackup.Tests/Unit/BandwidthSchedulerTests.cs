using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for <see cref="BandwidthScheduler"/> covering AIMD behavior,
/// stall/error fast-path decreases, plateau holds, and clamp bounds.
/// </summary>
public class BandwidthSchedulerTests
{
    /// <summary>
    /// Test clock that advances explicitly via <see cref="Advance"/>. Lets
    /// each test deterministically cross the scheduler's minimum sample
    /// window without sleeping.
    /// </summary>
    private sealed class FakeClock
    {
        private long _ticks;
        public long NowTicks() => Volatile.Read(ref _ticks);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, (long)delta.TotalMilliseconds);
    }

    [Fact]
    public void DefaultConstructionStartsAtInitialConnections()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 4, minConnections: 2, maxConnections: 32);

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(2, scheduler.MinConnections);
        Assert.Equal(32, scheduler.MaxConnections);
    }

    [Fact]
    public void ConstructorRejectsInitialBelowMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BandwidthScheduler(initialConnections: 1, minConnections: 4, maxConnections: 32));
    }

    [Fact]
    public void ConstructorRejectsInitialAboveMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BandwidthScheduler(initialConnections: 64, minConnections: 2, maxConnections: 32));
    }

    [Fact]
    public void ConstructorRejectsMaxBelowMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BandwidthScheduler(initialConnections: 2, minConnections: 8, maxConnections: 4));
    }

    [Fact]
    public void ConstructorRejectsZeroInitial()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BandwidthScheduler(initialConnections: 0, minConnections: 1, maxConnections: 8));
    }

    [Fact]
    public void RecordZeroBytesIsNoop()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(0);
        scheduler.RecordBytesCompleted(-100);

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(0, scheduler.AdditiveIncreases);
        Assert.Equal(0, scheduler.MultiplicativeDecreases);
    }

    [Fact]
    public void FirstSampleSeedsEwmaWithoutChangingConnections()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(10_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.True(scheduler.EwmaBytesPerSecond > 0);
    }

    [Fact]
    public void ImprovingThroughputTriggersAdditiveIncrease()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        // Seed EWMA with a low baseline.
        scheduler.RecordBytesCompleted(1_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        // Now record substantially more bytes in the same window.
        scheduler.RecordBytesCompleted(10_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(5, scheduler.CurrentConnections);
        Assert.Equal(1, scheduler.AdditiveIncreases);
    }

    [Fact]
    public void RegressingThroughputTriggersMultiplicativeDecrease()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(8, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(10_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        scheduler.RecordBytesCompleted(1_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(1, scheduler.MultiplicativeDecreases);
    }

    [Fact]
    public void PlateauThroughputHolds()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(5_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        // Same throughput as the seed window.
        scheduler.RecordBytesCompleted(5_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(0, scheduler.AdditiveIncreases);
        Assert.Equal(0, scheduler.MultiplicativeDecreases);
        Assert.True(scheduler.Holds >= 1);
    }

    [Fact]
    public void StallNotificationHalvesImmediately()
    {
        var scheduler = new BandwidthScheduler(8, 2, 32);

        scheduler.NotifyStallObserved();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(1, scheduler.MultiplicativeDecreases);
        Assert.Equal(1, scheduler.StallSignals);
    }

    [Fact]
    public void TransientErrorNotificationHalvesImmediately()
    {
        var scheduler = new BandwidthScheduler(16, 2, 32);

        scheduler.NotifyTransientError();

        Assert.Equal(8, scheduler.CurrentConnections);
        Assert.Equal(1, scheduler.MultiplicativeDecreases);
        Assert.Equal(1, scheduler.ErrorSignals);
    }

    [Fact]
    public void MultiplicativeDecreaseRespectsMinFloor()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 2, minConnections: 2, maxConnections: 32);

        scheduler.NotifyStallObserved();
        scheduler.NotifyStallObserved();
        scheduler.NotifyStallObserved();

        Assert.Equal(2, scheduler.CurrentConnections);
    }

    [Fact]
    public void AdditiveIncreaseRespectsMaxCeiling()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(initialConnections: 4, minConnections: 2, maxConnections: 4, nowTicksProvider: clock.NowTicks);

        scheduler.RecordBytesCompleted(1_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        scheduler.RecordBytesCompleted(10_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(0, scheduler.AdditiveIncreases);
    }

    [Fact]
    public void EvaluationBeforeMinSampleWindowDoesNotFire()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(1_000_000);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        scheduler.RecordBytesCompleted(1_000_000);

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.Equal(0, scheduler.AdditiveIncreases);
        Assert.Equal(0, scheduler.MultiplicativeDecreases);
        Assert.Equal(0, scheduler.Holds);
    }

    [Fact]
    public void EmptySampleWindowAfterSeedHolds()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 32, clock.NowTicks);

        scheduler.RecordBytesCompleted(5_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        Assert.Equal(4, scheduler.CurrentConnections);
        Assert.True(scheduler.Holds >= 1);
    }

    [Fact]
    public void RepeatedAdditiveIncreasesConvergeToMax()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(4, 2, 8, clock.NowTicks);

        // Seed with low throughput, then keep doubling reported bytes so each
        // window beats the previous EWMA by more than the plateau band.
        long bytes = 1_000_000;
        scheduler.RecordBytesCompleted(bytes);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        for (var i = 0; i < 20; i++)
        {
            bytes *= 2;
            scheduler.RecordBytesCompleted(bytes);
            clock.Advance(TimeSpan.FromSeconds(3));
            scheduler.ForceEvaluate();
        }

        Assert.Equal(8, scheduler.CurrentConnections);
    }

    [Fact]
    public async Task AcquireSlotAsyncWithinLimitReturnsImmediately()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 4, minConnections: 2, maxConnections: 32);

        await using var slotA = await scheduler.AcquireSlotAsync(CancellationToken.None);
        await using var slotB = await scheduler.AcquireSlotAsync(CancellationToken.None);

        Assert.Equal(2, scheduler.ActiveSlots);
    }

    [Fact]
    public async Task ReleasingSlotAllowsAnotherToBeAcquired()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 1, minConnections: 1, maxConnections: 4);

        var slotA = await scheduler.AcquireSlotAsync(CancellationToken.None);

        var blockedAcquire = scheduler.AcquireSlotAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(blockedAcquire.IsCompleted);

        await slotA.DisposeAsync();
        var slotB = await blockedAcquire;
        try
        {
            Assert.Equal(1, scheduler.ActiveSlots);
        }
        finally
        {
            await slotB.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlotHandleDisposeIsIdempotent()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 1, minConnections: 1, maxConnections: 4);

        var slot = await scheduler.AcquireSlotAsync(CancellationToken.None);
        await slot.DisposeAsync();
        await slot.DisposeAsync();

        Assert.Equal(0, scheduler.ActiveSlots);
    }

    [Fact]
    public async Task AcquireSlotAsyncCancellationThrowsAndDoesNotAdvanceCount()
    {
        var scheduler = new BandwidthScheduler(initialConnections: 1, minConnections: 1, maxConnections: 4);

        var slotA = await scheduler.AcquireSlotAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var pending = scheduler.AcquireSlotAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        Assert.Equal(1, scheduler.ActiveSlots);
        await slotA.DisposeAsync();
    }

    [Fact]
    public async Task AdditiveIncreaseWakesParkedAcquirer()
    {
        var clock = new FakeClock();
        var scheduler = new BandwidthScheduler(initialConnections: 1, minConnections: 1, maxConnections: 4, nowTicksProvider: clock.NowTicks);

        // Saturate.
        var slotA = await scheduler.AcquireSlotAsync(CancellationToken.None);

        var pending = scheduler.AcquireSlotAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        // Drive an additive-increase step. The scheduler should release one
        // waiter wakeup so the parked Task can complete without anyone
        // having to release a slot.
        scheduler.RecordBytesCompleted(1_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        scheduler.RecordBytesCompleted(10_000_000);
        clock.Advance(TimeSpan.FromSeconds(3));
        scheduler.ForceEvaluate();

        var slotB = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Equal(2, scheduler.ActiveSlots);
        }
        finally
        {
            await slotA.DisposeAsync();
            await slotB.DisposeAsync();
        }
    }
}
