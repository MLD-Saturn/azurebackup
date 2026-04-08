namespace AzureBackup.Core;

/// <summary>
/// Cross-platform helper to detect physical and available system memory.
/// Uses GCMemoryInfo on all platforms (no P/Invoke or /proc parsing needed).
/// All memory values are in bytes for consistency with the rest of the codebase.
/// Use <see cref="FormatHelper.FormatBytes"/> for display formatting.
/// </summary>
public static class SystemMemoryHelper
{
    private const long MB = 1024L * 1024;

    /// <summary>
    /// Minimum memory limit the user can select (512 MB).
    /// </summary>
    public const int MinLimitMB = 512;

    /// <summary>
    /// Gets the total physical memory installed in the system, in bytes.
    /// Returns 0 if detection fails.
    /// </summary>
    public static long GetTotalPhysicalMemoryBytes()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the estimated available memory (not committed by other processes), in bytes.
    /// This is an approximation: total physical minus the current GC heap size,
    /// minus a safety margin for OS and other processes (~25% of total or 1 GB, whichever is larger).
    /// </summary>
    public static long GetEstimatedAvailableMemoryBytes()
    {
        try
        {
            var totalBytes = GetTotalPhysicalMemoryBytes();
            var heapBytes = GC.GetTotalMemory(forceFullCollection: false);

            var osReserve = Math.Max(totalBytes / 4, 1024L * MB);
            var available = totalBytes - heapBytes - osReserve;
            return Math.Max(available, 0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Generates the stepped slider detents (powers of 2 in MB) from 512 MB up to total physical RAM.
    /// Values are in MB because the slider displays user-friendly stepped values.
    /// </summary>
    public static int[] GetMemorySteps(long totalPhysicalBytes)
    {
        var totalMB = (int)(totalPhysicalBytes / MB);
        if (totalMB <= 0)
            return [MinLimitMB];

        List<int> steps = [];
        var step = MinLimitMB;
        while (step <= totalMB)
        {
            steps.Add(step);
            if (step >= totalMB)
                break;
            step *= 2;
        }

        // Ensure the last step doesn't exceed total RAM
        if (steps.Count == 0 || steps[^1] < totalMB)
        {
            if (steps.Count == 0 || steps[^1] != totalMB)
                steps.Add(totalMB);
        }

        return [.. steps];
    }

    /// <summary>
    /// Determines the color category for the selected memory limit.
    /// </summary>
    public static MemoryLimitSeverity GetSeverity(int selectedMB, long totalPhysicalBytes)
    {
        if (totalPhysicalBytes <= 0)
            return MemoryLimitSeverity.Safe;

        var ratio = (double)(selectedMB * MB) / totalPhysicalBytes;
        return ratio switch
        {
            <= 0.5 => MemoryLimitSeverity.Safe,
            <= 0.8 => MemoryLimitSeverity.Aggressive,
            _ => MemoryLimitSeverity.Dangerous
        };
    }
}

/// <summary>
/// Severity level for the memory limit selection indicator.
/// </summary>
public enum MemoryLimitSeverity
{
    /// <summary>At or below 50% of physical RAM — safe for most workloads.</summary>
    Safe,

    /// <summary>Between 50% and 80% — aggressive, may cause paging under load.</summary>
    Aggressive,

    /// <summary>Above 80% — high risk of OOM or heavy swap.</summary>
    Dangerous
}
