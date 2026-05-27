using System.IO;
using AzureBackup.Benchmarks;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B75: tests for <see cref="BenchmarkSummaryWriter"/>'s pure helpers
/// and the file-IO entry point's no-throw contract. The full
/// <see cref="BenchmarkDotNet.Reports.Summary"/> shape is awkward to
/// construct in a unit test (the BDN ctor takes BenchmarkReports
/// which require a full BenchmarkCase + Descriptor + Job), so the
/// summary-against-real-Summary path is left to manual / end-to-end
/// validation when the user re-runs the benchmark. These tests pin
/// the formatters and the no-throw entry-point contract that protect
/// a multi-hour benchmark from losing artifacts to a post-run hook
/// failure.
/// </summary>
public sealed class BenchmarkSummaryWriterTests
{
    [Theory]
    [InlineData(null, "-")]
    [InlineData(0.0, "-")]
    [InlineData(-1.0, "-")]
    [InlineData(double.NaN, "-")]
    [InlineData(500.0, "500.00 ns")]
    [InlineData(1_500.0, "1.50 us")]
    [InlineData(2_500_000.0, "2.50 ms")]
    [InlineData(3_500_000_000.0, "3.50 s")]
    [InlineData(120_000_000_000.0, "2.00 m")]
    public void FormatNanosProducesStableShape(double? ns, string expected)
    {
        Assert.Equal(expected, BenchmarkSummaryWriter.FormatNanos(ns));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0L, "-")]
    [InlineData(-1L, "-")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2.00 KB")]
    [InlineData(5L * 1024 * 1024, "5.00 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void FormatBytesProducesStableShape(long? bytes, string expected)
    {
        Assert.Equal(expected, BenchmarkSummaryWriter.FormatBytes(bytes));
    }

    [Fact]
    public void FormatDurationReturnsDashForZeroOrNegative()
    {
        Assert.Equal("-", BenchmarkSummaryWriter.FormatDuration(TimeSpan.Zero));
        Assert.Equal("-", BenchmarkSummaryWriter.FormatDuration(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void FormatDurationFormatsSeconds()
    {
        Assert.Equal("3.5s", BenchmarkSummaryWriter.FormatDuration(TimeSpan.FromSeconds(3.5)));
    }

    [Fact]
    public void FormatDurationFormatsMinutes()
    {
        Assert.Equal("2m 30s", BenchmarkSummaryWriter.FormatDuration(TimeSpan.FromSeconds(150)));
    }

    [Fact]
    public void FormatDurationFormatsHours()
    {
        Assert.Equal("7h 01m 35s", BenchmarkSummaryWriter.FormatDuration(
            new TimeSpan(hours: 7, minutes: 1, seconds: 35)));
    }

    [Fact]
    public void WriteAllReturnsEmptyForEmptyInput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "B75_WriteAllEmpty_" + Guid.NewGuid());
        try
        {
            var written = BenchmarkSummaryWriter.WriteAll([], dir);
            Assert.Empty(written);
            Assert.True(Directory.Exists(dir), "Output directory should be created even when there is nothing to write.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteAllDoesNotThrowOnInvalidOutputDirectory()
    {
        // The no-throw contract MUST hold even when the output path is
        // structurally invalid; the writer's whole point is to never
        // tear down a multi-hour benchmark with no surviving artifacts.
        // We expect WriteAll to surface the error on Console.Error and
        // return an empty list rather than propagating.
        var invalidDir = Path.Combine(Path.GetTempPath(), new string('?', 250));

        var prevErr = Console.Error;
        try
        {
            using var sink = new StringWriter();
            Console.SetError(sink);
            var written = BenchmarkSummaryWriter.WriteAll([], invalidDir);
            Assert.Empty(written);
        }
        finally
        {
            Console.SetError(prevErr);
        }
    }

    [Fact]
    public void WriteAllNullSummariesThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => BenchmarkSummaryWriter.WriteAll(null!, "ignored"));
    }

    [Fact]
    public void WriteAllEmptyDirectoryThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkSummaryWriter.WriteAll([], ""));
        Assert.Throws<ArgumentException>(() => BenchmarkSummaryWriter.WriteAll([], "   "));
    }

    [Fact]
    public void SummaryFileSuffixIsCommittableMarkdown()
    {
        // Pin the suffix shape so a future rename has to touch both the
        // production code and this test. The "-summary.md" suffix is
        // important: it sorts next to the BDN-generated "-report.csv"
        // / "-report.html" / "-report-github.md" in a directory listing,
        // and the .md extension makes GitHub render it natively in the
        // PR view that compares benchmark runs across commits.
        Assert.Equal("-summary.md", BenchmarkSummaryWriter.SummaryFileSuffix);
    }
}
