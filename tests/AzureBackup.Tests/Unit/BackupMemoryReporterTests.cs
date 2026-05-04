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
        // When a LargeChunkBufferPool is wired, the [mem] line MUST
        // surface its current cached residency, peak cached residency,
        // global-cap drop count, and hit rate so the user can see the
        // pool's contribution to the reported unaccounted gap.
        using var budget = new MemoryBudget(1024 * 1024);
        using var pool = new LargeChunkBufferPool(maxCachedBytes: 64L * 1024 * 1024);
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
}
