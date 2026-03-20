namespace AzureBackup.Core;

/// <summary>
/// Shared formatting utilities used across the application.
/// </summary>
public static class FormatHelper
{
    private static readonly string[] ByteSizes = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 GB").
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when bytes is negative.</exception>
    public static string FormatBytes(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        var order = 0;
        double size = bytes;
        while (size >= 1024)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {ByteSizes[order]}";
    }

    /// <summary>
    /// Formats a duration in seconds into a human-readable string (e.g. "2m 30s", "1h 15m").
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when totalSeconds is negative, NaN, or Infinity.</exception>
    public static string FormatDuration(double totalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSeconds);

        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
            throw new ArgumentOutOfRangeException(nameof(totalSeconds), totalSeconds, "Value must be a finite number.");

        if (totalSeconds < 60)
            return $"{totalSeconds:F0}s";

        if (totalSeconds < 3600)
        {
            var minutes = (int)(totalSeconds / 60);
            var seconds = (int)(totalSeconds % 60);
            return $"{minutes}m {seconds}s";
        }

        var hours = (int)(totalSeconds / 3600);
        var remainingMinutes = (int)((totalSeconds % 3600) / 60);
        return $"{hours}h {remainingMinutes}m";
    }
}
