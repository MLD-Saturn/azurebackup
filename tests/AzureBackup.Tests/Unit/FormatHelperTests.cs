using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for FormatHelper formatting utilities.
/// </summary>
public class FormatHelperTests
{
    #region FormatBytes

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    [InlineData(1125899906842624, "1 PB")]
    [InlineData(1152921504606846976, "1 EB")]
    public void WhenFormatBytesThenReturnsHumanReadableSize(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatBytes(bytes));
    }

    [Fact]
    public void WhenFormatBytesWithLargeValueThenFormatsCorrectUnit()
    {
        Assert.Equal("2 TB", FormatHelper.FormatBytes(2L * 1099511627776));
    }

    [Fact]
    public void WhenFormatBytesWithLongMaxValueThenFormatsAsEB()
    {
        var result = FormatHelper.FormatBytes(long.MaxValue);
        Assert.EndsWith("EB", result);
    }

    [Fact]
    public void WhenFormatBytesNegativeThenThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatHelper.FormatBytes(-1));
    }

    #endregion

    #region FormatDuration

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(30, "30s")]
    [InlineData(59, "59s")]
    public void WhenDurationUnderOneMinuteThenFormatsAsSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(60, "1m 0s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3599, "59m 59s")]
    public void WhenDurationUnderOneHourThenFormatsAsMinutesAndSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(3600, "1h 0m")]
    [InlineData(5400, "1h 30m")]
    [InlineData(7200, "2h 0m")]
    public void WhenDurationOneHourOrMoreThenFormatsAsHoursAndMinutes(double seconds, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatDuration(seconds));
    }

    [Fact]
    public void WhenDurationNegativeThenThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatHelper.FormatDuration(-1));
    }

    [Fact]
    public void WhenDurationNaNThenThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatHelper.FormatDuration(double.NaN));
    }

    [Fact]
    public void WhenDurationInfinityThenThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatHelper.FormatDuration(double.PositiveInfinity));
    }

    #endregion
}
