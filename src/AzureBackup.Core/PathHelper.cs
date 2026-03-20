namespace AzureBackup.Core;

/// <summary>
/// Shared path manipulation utilities.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Gets the relative path from a known base path.
    /// Normalizes both paths and falls back to the filename if the path is not under the base.
    /// </summary>
    public static string GetRelativePathFromBase(string fullPath, string basePath)
    {
        fullPath = Path.GetFullPath(fullPath);
        basePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[basePath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? Path.GetFileName(fullPath) : relative;
        }

        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Finds the longest common root directory among a list of paths.
    /// Returns empty string if no common root exists.
    /// </summary>
    public static string FindCommonRoot(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return string.Empty;

        var directories = paths
            .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        if (directories.Count == 0)
            return string.Empty;

        var commonRoot = directories[0];
        foreach (var dir in directories.Skip(1))
        {
            while (!dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase) && commonRoot.Length > 0)
            {
                var parentDir = Path.GetDirectoryName(commonRoot);
                if (string.IsNullOrEmpty(parentDir) || parentDir == commonRoot)
                {
                    commonRoot = string.Empty;
                    break;
                }
                commonRoot = parentDir;
            }
        }

        return commonRoot;
    }

    /// <summary>
    /// Gets a display name for a path, handling drive roots (e.g. "J:\") where
    /// Path.GetFileName returns empty string.
    /// </summary>
    public static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(name))
            return trimmed + Path.DirectorySeparatorChar;

        return name;
    }
}
