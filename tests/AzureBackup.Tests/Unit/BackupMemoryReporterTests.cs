using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B36: tests for <see cref="BackupMemoryReporter"/>.
///
/// <para>
/// The reporter is a thin observer of <see cref="MemoryBudget"/> and the
/// .NET runtime's memory APIs; the assertions focus on contract behaviour
/// (cadence, initial sample, terminal sample on dispose, swallowed
/// exceptions) rather than the exact text of the emitted line. Asserting
/// against the textual format would couple the tests to the log shape
/// and make every cosmetic improvement break the build.
/// </para>
/// </summary>
public class BackupMemoryReporterTests
{
    [Fact]
    public void ConstructorEmitsInitialSampleSynchronously()
    {
        // The first sample is emitted from the constructor (not from the
        // first timer tick) so even sub-second operations capture a
        // starting memory snapshot. Failing this would mean a fast
        // backup leaves no memory data in the log at all.
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10)); // long interval so timer never fires

        Assert.Single(lines);
        Assert.StartsWith("[mem-start]", lines[0]);
        Assert.Contains("test", lines[0]);
    }

    [Fact]
    public void DisposeEmitsTerminalSample()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        Assert.Single(lines); // initial sample only
        reporter.Dispose();

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("[mem-start]", lines[0]);
        Assert.StartsWith("[mem]", lines[1]);
    }

    [Fact]
    public async Task EmitsPeriodicSamplesAtConfiguredInterval()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();
        var sync = new object();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: line => { lock (sync) { lines.Add(line); } },
            interval: TimeSpan.FromMilliseconds(50));

        // Wait long enough for at least 3 ticks to fire on top of the
        // initial sample. 250 ms / 50 ms = 5 expected, so >=4 is a safe
        // lower bound that tolerates timer-callback scheduling jitter
        // on a busy CI host.
        await Task.Delay(250);

        int count;
        lock (sync) { count = lines.Count; }
        Assert.True(count >= 4, $"Expected at least 4 samples, got {count}");
    }

    [Fact]
    public void ReportLineIncludesBudgetUsage()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        Assert.Contains("budget", lines[0]);
        Assert.Contains("workingSet", lines[0]);
        Assert.Contains("gcHeap", lines[0]);
        Assert.Contains("stalls", lines[0]);
        Assert.Contains("oversized", lines[0]);
        Assert.Contains("unaccounted", lines[0]);
    }

    [Fact]
    public void ReportLineLabelsUnlimitedBudgetExplicitly()
    {
        using var budget = new MemoryBudget(long.MaxValue);
        var lines = new List<string>();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        Assert.Contains("unlimited", lines[0]);
    }

    [Fact]
    public void EmitExceptionDoesNotPropagateOutOfTimerCallback()
    {
        // The reporter must never let an emit exception escape into
        // the timer callback -- doing so would tear down the process
        // via TaskScheduler.UnobservedTaskException. We test by
        // throwing from the emit delegate and confirming Dispose
        // returns normally.
        using var budget = new MemoryBudget(1024 * 1024);

        // Initial-sample throw must surface (we have not entered async
        // territory yet). Subsequent timer ticks must be swallowed.
        var throwOnNthCall = 2;
        var calls = 0;

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: line =>
            {
                if (Interlocked.Increment(ref calls) >= throwOnNthCall)
                {
                    throw new InvalidOperationException("simulated emit failure");
                }
            },
            interval: TimeSpan.FromMilliseconds(20));

        // Let the timer fire enough times to exercise the swallow path.
        Thread.Sleep(120);
        // Dispose must not throw.
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        reporter.Dispose();
        reporter.Dispose(); // second call must be a no-op

        // initial + one terminal = 2; second Dispose adds nothing.
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void NullBudgetThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BackupMemoryReporter(null!, "test", _ => { }));
    }

    [Fact]
    public void NullEmitThrows()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        Assert.Throws<ArgumentNullException>(() =>
            new BackupMemoryReporter(budget, "test", null!));
    }

    [Fact]
    public void EmptyOpLabelThrows()
    {
        using var budget = new MemoryBudget(1024 * 1024);
        Assert.Throws<ArgumentException>(() =>
            new BackupMemoryReporter(budget, "", _ => { }));
    }

    // ---- B56 (W3 Phase F): peak-aware telemetry ----

    [Fact]
    public async Task ReportLineIncludesBudgetPeak()
    {
        // After acquiring then releasing more than half the budget,
        // the next emitted sample MUST advertise the peak alongside
        // the (now-lower) current usage so the operator can see how
        // close the operation came to saturating the configured ceiling.
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        await budget.AcquireAsync(800 * 1024);
        budget.Release(800 * 1024);

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        Assert.Contains("peak=", lines[0]);
    }

    [Fact]
    public void ReportLineIncludesPoolTelemetryWhenPoolWired()
    {
        // When a ChunkBufferPool is wired on the large-chunk slot,
        // the [mem] line MUST surface its current cached residency,
        // peak cached residency, global-cap drop count, and hit rate
        // so the user can see the pool's contribution to the
        // reported unaccounted gap.
        using var budget = new MemoryBudget(1024 * 1024);
        using var pool = new ChunkBufferPool(ChunkBufferPool.LargeChunkBucketSizes, maxCachedBytes: 64L * 1024 * 1024);
        var lines = new List<string>();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10),
            largeChunkPool: pool);

        Assert.Contains("lohPool=", lines[0]);
        Assert.Contains("peak=", lines[0]);
        Assert.Contains("dropped=", lines[0]);
        Assert.Contains("hit=", lines[0]);
    }

    [Fact]
    public void ReportLineOmitsPoolTelemetryWhenPoolNotWired()
    {
        // When no pool is wired the line MUST NOT include the lohPool
        // segment; emitting "lohPool=0 MB" would mislead callers into
        // thinking the pool is wired but unused.
        using var budget = new MemoryBudget(1024 * 1024);
        var lines = new List<string>();

        using var reporter = new BackupMemoryReporter(
            budget,
            opLabel: "test",
            emit: lines.Add,
            interval: TimeSpan.FromMinutes(10));

        Assert.DoesNotContain("lohPool=", lines[0]);
    }

    [Fact]
    public void UnaccountedDoesNotDoubleSubtractPoolRetentionAfterB72()
    {
        // B74 (W5 Phase 4 Commit 3, Fix A): pre-B74 the unaccounted formula
        // was workingSet - used - poolCachedNow - smallPoolCachedNow, but
        // after B72 `used` already INCLUDES the pool's retention via
        // ChargeRetention. The pre-B74 formula therefore double-subtracted
        // the pool cache and produced a too-low MaxUnacc reading. This
        // test pins the corrected formula via the extracted
        // ComputeUnaccountedBytes helper so the invariant holds
        // independent of Process.WorkingSet64 jitter.
        //
        // Scenario: workingSet = 100 MB, budget.UsedBytes = 30 MB
        // (10 MB in-flight + 20 MB pool retention via B72 charge),
        // largePool cached = 20 MB, smallPool cached = 0.
        // The correct unaccounted = 100 - (30 - 20) - 20 - 0 = 70 MB.
        // The pre-B74 buggy formula would have given 100 - 30 - 20 - 0 = 50 MB,
        // i.e. 20 MB too low (exactly the double-subtracted retention).
        long workingSet = 100L * 1024 * 1024;
        long budgetUsed = 30L * 1024 * 1024;       // includes 20 MB of pool retention
        long poolBudgetCharged = 20L * 1024 * 1024; // sum across both pools
        long largePoolCached = 20L * 1024 * 1024;
        long smallPoolCached = 0L;

        var unaccounted = BackupMemoryReporter.ComputeUnaccountedBytes(
            workingSet, budgetUsed, poolBudgetCharged, largePoolCached, smallPoolCached);

        Assert.Equal(70L * 1024 * 1024, unaccounted);
    }

    [Fact]
    public void UnaccountedFormulaIsBackwardsCompatibleWhenNoPoolWired()
    {
        // When no pool is wired BudgetChargedBytes is 0 and both pool
        // cache values are 0, so the formula MUST reduce to the pre-B72
        // shape workingSet - used. Non-pool callers' MaxUnacc readings
        // are completely unaffected by B74.
        long workingSet = 100L * 1024 * 1024;
        long budgetUsed = 30L * 1024 * 1024;

        var unaccounted = BackupMemoryReporter.ComputeUnaccountedBytes(
            workingSet, budgetUsed, poolBudgetChargedBytes: 0,
            largePoolCachedBytes: 0, smallPoolCachedBytes: 0);

        Assert.Equal(70L * 1024 * 1024, unaccounted);
    }

    [Fact]
    public void UnaccountedFormulaClampsToZero()
    {
        // When the budget tracks more bytes than the process actually
        // holds (transient: charge fired before allocation completed)
        // the formula must NOT report a negative number -- the line
        // would be confusing and the downstream MaxUnaccountedBytes
        // tracker would interpret negatives as huge unsigned values.
        long workingSet = 10L * 1024 * 1024;
        long budgetUsed = 100L * 1024 * 1024;

        var unaccounted = BackupMemoryReporter.ComputeUnaccountedBytes(
            workingSet, budgetUsed, poolBudgetChargedBytes: 0,
            largePoolCachedBytes: 0, smallPoolCachedBytes: 0);

        Assert.Equal(0, unaccounted);
    }

    [Fact]
    public void UnaccountedFormulaHandlesPoolBudgetChargeGreaterThanUsed()
    {
        // Defensive: if a race causes pool BudgetChargedBytes to be
        // momentarily larger than budget.UsedBytes (e.g. a Release
        // arrived between two reads), the usedExcludingPoolRetention
        // calculation must clamp to 0 rather than going negative.
        long workingSet = 50L * 1024 * 1024;
        long budgetUsed = 10L * 1024 * 1024;
        long poolBudgetCharged = 20L * 1024 * 1024; // larger than used
        long largePoolCached = 5L * 1024 * 1024;

        var unaccounted = BackupMemoryReporter.ComputeUnaccountedBytes(
            workingSet, budgetUsed, poolBudgetCharged, largePoolCached, smallPoolCachedBytes: 0);

        // usedExcludingPoolRetention clamps to 0, so unaccounted = 50 - 0 - 5 - 0 = 45.
        Assert.Equal(45L * 1024 * 1024, unaccounted);
    }
}
