using static AzureBackup.Core.ByteSizes;

namespace AzureBackup.Core;

/// <summary>
/// Cross-platform helper to detect physical and available system memory.
/// Uses GCMemoryInfo on all platforms (no P/Invoke or /proc parsing needed).
/// All memory values are in bytes for consistency with the rest of the codebase.
/// Use <see cref="FormatHelper.FormatBytes"/> for display formatting.
/// </summary>
public static class SystemMemoryHelper
{
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

            var osReserve = Math.Max(totalBytes / 4, KB * MBLong);
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

        // Ensure the last detent equals total RAM. The loop only ever appends
        // values <= totalMB, so reaching this branch guarantees steps[^1] != totalMB;
        // the previously-nested guard was always true and has been removed.
        if (steps.Count == 0 || steps[^1] < totalMB)
        {
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

        var ratio = (double)(selectedMB * MBLong) / totalPhysicalBytes;
        return ratio switch
        {
            <= 0.5 => MemoryLimitSeverity.Safe,
            <= 0.8 => MemoryLimitSeverity.Aggressive,
            _ => MemoryLimitSeverity.Dangerous
        };
    }

    /// <summary>
    /// Default upper bound (in MB) for the recommended memory limit on
    /// machines with abundant physical RAM. B29: chosen to match the
    /// B27 worst-case worker-pool ceiling
    /// (<c>MaxParallelFileBackups=16</c> x
    /// <c>MaxParallelChunkUploads=6</c> x <c>MaxChunkSize=64 MB</c> = 6 GB)
    /// plus ~2 GB of headroom for metadata, GC slack, and short-lived
    /// allocations. Above this value the budget is no longer pulling
    /// any extra weight -- the worker pool itself caps in-flight bytes
    /// at the same point. Users on workstations with vastly more RAM
    /// can still raise the slider manually past 8 GB up to total
    /// physical RAM; the cap only governs the auto-selected default
    /// for fresh installs.
    /// </summary>
    public const int RecommendedDefaultLimitCapMB = 8192;

    /// <summary>
    /// Computes the hardware-aware <c>MemoryLimitMB</c> default for a
    /// fresh install. Returns 25% of total physical RAM, snapped DOWN
    /// to the nearest stepped slider detent
    /// (see <see cref="GetMemorySteps(long)"/>), then clamped to
    /// [<see cref="MinLimitMB"/>, <see cref="RecommendedDefaultLimitCapMB"/>].
    /// <para>
    /// B29: the previous flat 8192 MB default
    /// (introduced in B27) misbehaved on low-RAM machines: on a 4 GB
    /// host the saved value of 8192 exceeded total physical RAM, so
    /// the budget never bound and the user saw a red Dangerous
    /// indicator on a slider that could only display up to 4 GB. The
    /// 25% rule produces a Safe-band value at every RAM tier from 2 GB
    /// up, lines up with the 25% OS reserve already used by
    /// <see cref="GetEstimatedAvailableMemoryBytes"/>, and is
    /// guaranteed to be a real slider detent so the UI does not show
    /// a value the user cannot otherwise pick.
    /// </para>
    /// <para>
    /// Sample outputs (snap-down to a step in {512, 1024, 2048, 4096,
    /// 8192, ...}, then cap at 8192):
    /// 2 GB total -&gt; 512 MB; 4 GB -&gt; 1024 MB; 8 GB -&gt; 2048 MB;
    /// 16 GB -&gt; 4096 MB; 32 GB -&gt; 8192 MB; 64 GB -&gt; 8192 MB
    /// (capped); 128 GB -&gt; 8192 MB (capped).
    /// </para>
    /// <para>
    /// Returns <see cref="RecommendedDefaultLimitCapMB"/> as a defensive
    /// fallback if total-physical-RAM detection fails -- matches the
    /// pre-B29 fixed default so behaviour does not regress when the
    /// runtime cannot answer.
    /// </para>
    /// </summary>
    public static int GetRecommendedDefaultLimitMB()
    {
        var totalBytes = GetTotalPhysicalMemoryBytes();
        return GetRecommendedDefaultLimitMBForTotalBytes(totalBytes);
    }

    /// <summary>
    /// Pure-function sibling of <see cref="GetRecommendedDefaultLimitMB"/>
    /// that takes the total-physical-RAM input explicitly so unit tests
    /// can exercise every RAM tier deterministically without depending
    /// on the host machine.
    /// </summary>
    public static int GetRecommendedDefaultLimitMBForTotalBytes(long totalPhysicalBytes)
    {
        if (totalPhysicalBytes <= 0)
        {
            // Detection failed -- fall back to the historical fixed
            // default from B27 so behaviour does not regress.
            return RecommendedDefaultLimitCapMB;
        }

        // 25% of total RAM, in MB.
        var quarterMB = (int)((totalPhysicalBytes / 4) / MB);

        // Snap DOWN to the largest step that does not exceed quarterMB.
        // Snap-down (not nearest) so the auto-default is always
        // strictly within the Safe band for any RAM size, including
        // edge cases where 25% lands between two steps.
        var steps = GetMemorySteps(totalPhysicalBytes);
        var snapped = MinLimitMB;
        foreach (var step in steps)
        {
            if (step <= quarterMB)
                snapped = step;
            else
                break;
        }

        // Clamp to [MinLimitMB, RecommendedDefaultLimitCapMB].
        if (snapped < MinLimitMB) snapped = MinLimitMB;
        if (snapped > RecommendedDefaultLimitCapMB) snapped = RecommendedDefaultLimitCapMB;
        return snapped;
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
