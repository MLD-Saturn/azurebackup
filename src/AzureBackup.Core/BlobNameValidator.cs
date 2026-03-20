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
    /// </summary>
    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex ValidHashPattern();

    /// <summary>
    /// Validates that a chunk hash is a well-formed SHA-256 hex string.
    /// Prevents path traversal and injection attacks via blob names.
    /// </summary>
    /// <exception cref="SecurityPolicyException">Thrown when the hash is empty or malformed.</exception>
    public static void ValidateChunkHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new SecurityPolicyException("Chunk hash cannot be empty", SecurityPolicyType.InvalidBlobName);

        if (!ValidHashPattern().IsMatch(hash))
            throw new SecurityPolicyException($"Invalid chunk hash format: {hash}", SecurityPolicyType.InvalidBlobName);
    }
}
