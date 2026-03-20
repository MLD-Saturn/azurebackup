namespace AzureBackup.Core;

/// <summary>
/// Shared filesystem utilities.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    /// Recursively removes empty directories under the given path.
    /// Errors are silently ignored (best-effort cleanup).
    /// </summary>
    public static void CleanEmptyDirectories(string directory)
    {
        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            CleanEmptyDirectories(subDir);

            if (!Directory.EnumerateFileSystemEntries(subDir).Any())
            {
                try
                {
                    Directory.Delete(subDir);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }
}
