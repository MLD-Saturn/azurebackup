using System.Text.RegularExpressions;

namespace AzureBackup.Core;

/// <summary>
/// Validates blob names and hashes to prevent path traversal attacks.
/// Shared by all IBlobStorageService implementations.
/// </summary>
public static partial class BlobNameValidator
{
    /// <summary>
    /// Regex for validating chunk hashes (SHA-256 hex string, exactly 64 hex characters).
    /// Retained for external / diagnostic use; the hot-path validator uses a manual
    /// ASCII-hex scan (see <see cref="ValidateChunkHash"/>) that is ~10x faster.
    /// </summary>
    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex ValidHashPattern();

    /// <summary>
    /// Validates that a chunk hash is a well-formed SHA-256 hex string
    /// (exactly 64 characters, all 0-9 / a-f / A-F).
    /// Prevents path traversal and injection attacks via blob names.
    /// </summary>
    /// <exception cref="SecurityPolicyException">Thrown when the hash is empty or malformed.</exception>
    public static void ValidateChunkHash(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            throw new SecurityPolicyException("Chunk hash cannot be empty", SecurityPolicyType.InvalidBlobName);

        // Fast path: 64-char ASCII-hex check with no regex engine, no allocations.
        // SHA-256 hex is always 64 characters; reject length mismatches up front.
        if (hash.Length != 64 || !IsAsciiHex64(hash))
            throw new SecurityPolicyException($"Invalid chunk hash format: {hash}", SecurityPolicyType.InvalidBlobName);
    }

    /// <summary>
    /// Checks that every character in a length-64 string is an ASCII hex digit (0-9, a-f, A-F).
    /// Caller is responsible for verifying the length; this method assumes length == 64.
    /// </summary>
    private static bool IsAsciiHex64(string value)
    {
        // Unrolled scan over the 64 chars. char.IsAsciiHexDigit is a tight intrinsic
        // on .NET 7+ and inlines to a handful of comparisons.
        for (var i = 0; i < 64; i++)
        {
            if (!char.IsAsciiHexDigit(value[i]))
                return false;
        }
        return true;
    }
}

