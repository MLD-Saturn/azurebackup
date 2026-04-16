using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for chunk integrity verification during deduplication.
/// </summary>
public class ChunkIntegrityVerificationTests : IAsyncLifetime
{
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;

    private const string TestPassword = "TestPassword123!@#";

    public async Task InitializeAsync()
    {
        _encryptionService = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);

        _blobService = new InMemoryBlobService(_encryptionService);
        await _blobService.ConnectAsync("test-connection", "test-container");
    }

    public Task DisposeAsync()
    {
        _encryptionService?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyChunkIntegrityAsync_MatchingData_ReturnsTrue()
    {
        // Arrange
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        var hash = Convert.ToHexString(SHA256.HashData(chunkData)).ToLowerInvariant();

        // Upload the chunk first
        await _blobService.UploadChunkAsync(chunkData, hash, StorageTier.Hot);

        // Act
        var result = await _blobService.VerifyChunkIntegrityAsync(hash, chunkData);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyChunkIntegrityAsync_DifferentData_ThrowsHashCollisionException()
    {
        // Arrange
        var originalData = new byte[1024];
        Random.Shared.NextBytes(originalData);
        var hash = Convert.ToHexString(SHA256.HashData(originalData)).ToLowerInvariant();

        // Upload the original chunk
        await _blobService.UploadChunkAsync(originalData, hash, StorageTier.Hot);

        // Create different data (simulating a hash collision - impossible in practice but we test the detection)
        var differentData = new byte[1024];
        Random.Shared.NextBytes(differentData);

        // Act & Assert
        await Assert.ThrowsAsync<HashCollisionException>(
            () => _blobService.VerifyChunkIntegrityAsync(hash, differentData));
    }

    [Fact]
    public async Task VerifyChunkIntegrityAsync_DifferentSize_ThrowsHashCollisionException()
    {
        // Arrange
        var originalData = new byte[1024];
        Random.Shared.NextBytes(originalData);
        var hash = Convert.ToHexString(SHA256.HashData(originalData)).ToLowerInvariant();

        // Upload the original chunk
        await _blobService.UploadChunkAsync(originalData, hash, StorageTier.Hot);

        // Create data with different size
        var differentSizeData = new byte[2048];
        Random.Shared.NextBytes(differentSizeData);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HashCollisionException>(
            () => _blobService.VerifyChunkIntegrityAsync(hash, differentSizeData));
        
        Assert.Equal(2048, ex.ExpectedSize);
        Assert.Equal(1024, ex.StoredSize);
    }

    [Fact]
    public async Task VerifyChunkIntegrityAsync_ChunkNotFound_ThrowsException()
    {
        // Arrange
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        var hash = Convert.ToHexString(SHA256.HashData(chunkData)).ToLowerInvariant();

        // Don't upload the chunk

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _blobService.VerifyChunkIntegrityAsync(hash, chunkData));
    }

    [Fact]
    public async Task UploadChunkAsync_DuplicateWithMatchingData_Succeeds()
    {
        // Arrange
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        var hash = Convert.ToHexString(SHA256.HashData(chunkData)).ToLowerInvariant();

        // Act - Upload twice with same data
        var blobName1 = await _blobService.UploadChunkAsync(chunkData, hash, StorageTier.Hot);
        var blobName2 = await _blobService.UploadChunkAsync(chunkData, hash, StorageTier.Hot);

        // Assert - Both should succeed and return same blob name (dedup)
        Assert.Equal(blobName1, blobName2);
    }

    [Fact]
    public async Task UploadChunkAsync_DuplicateWithDifferentData_UploadsWithCollisionSuffix()
    {
        // Arrange
        var originalData = new byte[1024];
        Random.Shared.NextBytes(originalData);
        var hash = Convert.ToHexString(SHA256.HashData(originalData)).ToLowerInvariant();

        // Upload original
        var blobName1 = await _blobService.UploadChunkAsync(originalData, hash, StorageTier.Hot);

        // Create different data but use the same hash (simulating collision)
        var differentData = new byte[1024];
        Random.Shared.NextBytes(differentData);

        // Act - Second upload with different data should succeed with collision suffix
        var blobName2 = await _blobService.UploadChunkAsync(differentData, hash, StorageTier.Hot);

        // Assert
        Assert.Equal($"chunks/{hash}", blobName1);
        Assert.Equal($"chunks/{hash}_v2", blobName2);
        Assert.NotEqual(blobName1, blobName2);
        
        // Verify both chunks can be downloaded with correct content
        var downloaded1 = await _blobService.DownloadChunkAsync(blobName1);
        var downloaded2 = await _blobService.DownloadChunkAsync(blobName2);
        Assert.True(originalData.SequenceEqual(downloaded1));
        Assert.True(differentData.SequenceEqual(downloaded2));
    }

    [Fact]
    public async Task UploadChunkAsync_MultipleCollisions_IncrementsSuffix()
    {
        // Arrange
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant();

        var data1 = new byte[100];
        var data2 = new byte[100];
        var data3 = new byte[100];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);
        Random.Shared.NextBytes(data3);

        // Act - Upload three different data chunks with same hash
        var blobName1 = await _blobService.UploadChunkAsync(data1, hash, StorageTier.Hot);
        var blobName2 = await _blobService.UploadChunkAsync(data2, hash, StorageTier.Hot);
        var blobName3 = await _blobService.UploadChunkAsync(data3, hash, StorageTier.Hot);

        // Assert
        Assert.Equal($"chunks/{hash}", blobName1);
        Assert.Equal($"chunks/{hash}_v2", blobName2);
        Assert.Equal($"chunks/{hash}_v3", blobName3);
    }

    [Fact]
    public async Task UploadChunkAsync_DuplicateCollisionData_DedupsToExistingVersion()
    {
        // Phase 2 / P4 regression: when a chunk with data matching an existing _vN
        // version is uploaded again, the resolver must dedup to that version rather
        // than creating yet another _v(N+1) duplicate.
        // Arrange
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 2 })).ToLowerInvariant();
        var data1 = new byte[100];
        var data2 = new byte[100];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        // Seed the primary slot and _v2
        var primaryBlob = await _blobService.UploadChunkAsync(data1, hash, StorageTier.Hot);
        var v2Blob = await _blobService.UploadChunkAsync(data2, hash, StorageTier.Hot);
        Assert.Equal($"chunks/{hash}", primaryBlob);
        Assert.Equal($"chunks/{hash}_v2", v2Blob);

        // Act - re-upload the SAME bytes as _v2
        var dedupBlob = await _blobService.UploadChunkAsync(data2, hash, StorageTier.Hot);

        // Assert - resolver found the matching _v2 and deduped; did NOT create _v3
        Assert.Equal($"chunks/{hash}_v2", dedupBlob);
    }

    [Fact]
    public void HashCollisionException_ContainsCorrectInfo()
    {
        // Arrange & Act
        var ex = new HashCollisionException("abc123", 1000, 2000);

        // Assert
        Assert.Equal("abc123", ex.ChunkHash);
        Assert.Equal(1000, ex.ExpectedSize);
        Assert.Equal(2000, ex.StoredSize);
        Assert.Contains("abc123", ex.Message);
        Assert.Contains("CRITICAL", ex.Message);
    }

    [Fact]
    public async Task VerifyChunkIntegrityAsync_UsesConstantTimeComparison()
    {
        // This test verifies that even with matching sizes, 
        // different data is detected (the comparison happens)
        
        // Arrange
        var originalData = new byte[1024];
        originalData[0] = 0x00;
        var hash = Convert.ToHexString(SHA256.HashData(originalData)).ToLowerInvariant();

        await _blobService.UploadChunkAsync(originalData, hash, StorageTier.Hot);

        // Create data that differs only in last byte
        var almostSameData = (byte[])originalData.Clone();
        almostSameData[^1] ^= 0xFF; // Flip last byte

        // Act & Assert - Should detect the difference
        await Assert.ThrowsAsync<HashCollisionException>(
            () => _blobService.VerifyChunkIntegrityAsync(hash, almostSameData));
    }
}
