using System.Text.RegularExpressions;

namespace AzureBackup.Core;

/// <summary>
/// Provides glob/wildcard pattern matching for file and directory names.
/// Supports * (any characters) and ? (single character) wildcards.
/// </summary>
public static class GlobMatcher
{
    /// <summary>
    /// Timeout for regex operations to prevent ReDoS attacks.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Returns true if the input matches the glob pattern.
    /// Supports * (zero or more characters) and ? (exactly one character).
    /// Matching is case-insensitive.
    /// </summary>
    /// <param name="input">The string to test (e.g. a file name or relative path).</param>
    /// <param name="pattern">A glob pattern such as "*.log", "temp*", or "file?.txt".</param>
    public static bool IsMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input))
            return false;

        try
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*?")
                .Replace("\\?", ".") + "$";

            return Regex.IsMatch(input, regexPattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the name matches any of the given glob patterns.
    /// </summary>
    /// <param name="name">The string to test.</param>
    /// <param name="patterns">A list of glob patterns.</param>
    public static bool MatchesAny(string name, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            if (IsMatch(name, pattern))
                return true;
        }

        return false;
    }
}
