using System.Security.Cryptography;

namespace AzureBackup.Core;

/// <summary>
/// SHA-256 hashing utilities for content-addressable storage and integrity verification.
/// </summary>
public static class HashHelper
{
    /// <summary>
    /// Computes the SHA-256 hash of in-memory data, returning a hex string.
    /// Used for chunk content addressing and integrity verification.
    /// </summary>
    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file, returning a hex string.
    /// Uses streaming to avoid loading the entire file into memory.
    /// </summary>
    public static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
