using System.Security.Cryptography;
using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for HashHelper SHA-256 hashing utilities.
/// </summary>
public class HashHelperTests
{
    #region ComputeHash — in-memory

    [Fact]
    public void WhenComputeHashThenReturns64CharHexString()
    {
        var data = "hello world"u8.ToArray();
        var result = HashHelper.ComputeHash(data);

        Assert.Equal(64, result.Length);
        Assert.Matches("^[A-F0-9]{64}$", result);
    }

    [Fact]
    public void WhenSameDataThenReturnsSameHash()
    {
        var data = "deterministic"u8.ToArray();
        var hash1 = HashHelper.ComputeHash(data);
        var hash2 = HashHelper.ComputeHash(data);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void WhenDifferentDataThenReturnsDifferentHash()
    {
        var hash1 = HashHelper.ComputeHash("data1"u8.ToArray());
        var hash2 = HashHelper.ComputeHash("data2"u8.ToArray());

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void WhenEmptyDataThenReturnsHashOfEmpty()
    {
        var result = HashHelper.ComputeHash(ReadOnlySpan<byte>.Empty);

        // SHA-256 of empty input is a well-known value
        var expected = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty));
        Assert.Equal(expected, result);
    }

    #endregion

    #region ComputeFileHashAsync

    [Fact]
    public async Task WhenComputeFileHashThenMatchesInMemoryHash()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"HashHelperTest_{Guid.NewGuid():N}.tmp");
        try
        {
            var content = "file content for hashing"u8.ToArray();
            await File.WriteAllBytesAsync(tempFile, content);

            var fileHash = await HashHelper.ComputeFileHashAsync(tempFile);
            var memoryHash = HashHelper.ComputeHash(content);

            Assert.Equal(memoryHash, fileHash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WhenFilePathNullThenThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => HashHelper.ComputeFileHashAsync(null!));
    }

    [Fact]
    public async Task WhenFilePathWhitespaceThenThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => HashHelper.ComputeFileHashAsync("   "));
    }

    [Fact]
    public async Task WhenCancellationRequestedThenThrowsOperationCanceledException()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"HashHelperTest_{Guid.NewGuid():N}.tmp");
        try
        {
            // Create a file large enough to not complete instantly
            await File.WriteAllBytesAsync(tempFile, new byte[1024]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => HashHelper.ComputeFileHashAsync(tempFile, cts.Token));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion
}
