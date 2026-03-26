using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for InMemoryBlobService (and by extension, IBlobStorageService contract).
/// Tests validation, deduplication, and storage behavior.
/// </summary>
public class BlobServiceTests : IAsyncLifetime
{
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    
    private const string TestPassword = "BlobServiceTestPassword123!";

    public async Task InitializeAsync()
    {
        _encryptionService = new EncryptionService();
        
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
        
        _blobService = new InMemoryBlobService(_encryptionService);
        await _blobService.ConnectAsync("fake-connection-string", "test-container");
    }

    public Task DisposeAsync()
    {
        _encryptionService.Dispose();
        return Task.CompletedTask;
    }

    #region Connection Tests

    [Fact]
    public async Task ConnectAsync_ValidParameters_SetsIsConnected()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        Assert.False(blobService.IsConnected);

        // Act
        await blobService.ConnectAsync("connection", "container");

        // Assert
        Assert.True(blobService.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_EmptyConnectionString_Throws()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            blobService.ConnectAsync("", "container"));
    }

    [Fact]
    public async Task ConnectAsync_EmptyContainerName_Throws()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            blobService.ConnectAsync("connection", ""));
    }

    [Fact]
    public async Task TestConnectionAsync_ValidParameters_ReturnsSuccess()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);

        // Act
        var (success, message) = await blobService.TestConnectionAsync("connection", "container");

        // Assert
        Assert.True(success);
        Assert.Contains("successful", message.ToLower());
    }

    [Fact]
    public async Task TestConnectionAsync_EmptyConnectionString_ReturnsFailure()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);

        // Act
        var (success, _) = await blobService.TestConnectionAsync("", "container");

        // Assert
        Assert.False(success);
    }

    #endregion

    #region Upload Tests

    [Fact]
    public async Task UploadChunkAsync_ValidData_ReturnsValidBlobName()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act
        var blobName = await _blobService.UploadChunkAsync(data, hash);

        // Assert
        Assert.StartsWith("chunks/", blobName);
        Assert.Contains(hash, blobName);
    }

    [Fact]
    public async Task UploadChunkAsync_NullData_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _blobService.UploadChunkAsync(null!, "somehash"));
    }

    [Fact]
    public async Task UploadChunkAsync_EmptyHash_ThrowsSecurityPolicyException()
    {
        // Arrange
        var data = CreateRandomContent(1024);

        // Act & Assert - Empty hash throws SecurityPolicyException (validation)
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkAsync(data, ""));
    }

    [Fact]
    public async Task UploadChunkAsync_SameDataTwice_DeduplicatesAndReturnsSameBlobName()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act
        var blobName1 = await _blobService.UploadChunkAsync(data, hash);
        var storageBefore = _blobService.TotalStorageUsed;
        
        var blobName2 = await _blobService.UploadChunkAsync(data, hash);
        var storageAfter = _blobService.TotalStorageUsed;

        // Assert
        Assert.Equal(blobName1, blobName2);
        Assert.Equal(storageBefore, storageAfter); // No additional storage used
    }

    [Fact]
    public async Task UploadChunkAsync_DifferentData_CreatesDifferentBlobs()
    {
        // Arrange
        var data1 = CreateRandomContent(1024);
        var hash1 = ComputeHash(data1);
        var data2 = CreateRandomContent(1024);
        var hash2 = ComputeHash(data2);

        // Act
        var blobName1 = await _blobService.UploadChunkAsync(data1, hash1);
        var blobName2 = await _blobService.UploadChunkAsync(data2, hash2);

        // Assert
        Assert.NotEqual(blobName1, blobName2);
    }


    [Fact]
    public async Task UploadChunkAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        var data = CreateRandomContent(10 * 1024);
        var hash = ComputeHash(data);
        long reportedBytes = 0;
        SynchronousProgress<long> progress = new(bytes => reportedBytes = bytes);

        // Act
        await _blobService.UploadChunkAsync(data, hash, progress: progress);

        // Assert
        Assert.True(reportedBytes > 0);
    }

    [Fact]
    public async Task UploadChunkAsync_UpdatesTotalBytesUploaded()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var initialBytes = _blobService.TotalBytesUploaded;

        // Act
        await _blobService.UploadChunkAsync(data, hash);

        // Assert
        Assert.True(_blobService.TotalBytesUploaded > initialBytes);
    }

    [Fact]
    public async Task UploadChunkAsync_UpdatesTotalOperations()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var initialOps = _blobService.TotalOperations;

        // Act
        await _blobService.UploadChunkAsync(data, hash);

        // Assert
        Assert.Equal(initialOps + 1, _blobService.TotalOperations);
    }

    #endregion

    #region UploadChunkDirectAsync Tests

    [Fact]
    public async Task UploadChunkDirectAsync_ValidData_ReturnsValidBlobName()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act
        var blobName = await _blobService.UploadChunkDirectAsync(data, hash);

        // Assert
        Assert.StartsWith("chunks/", blobName);
        Assert.Contains(hash, blobName);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_NullData_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _blobService.UploadChunkDirectAsync(null!, "somehash"));
    }

    [Fact]
    public async Task UploadChunkDirectAsync_EmptyHash_ThrowsSecurityPolicyException()
    {
        // Arrange
        var data = CreateRandomContent(1024);

        // Act & Assert
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkDirectAsync(data, ""));
    }

    [Fact]
    public async Task UploadChunkDirectAsync_SameDataTwice_OverwritesWithoutError()
    {
        // Arrange - Direct upload should overwrite, not deduplicate
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act - Upload twice
        var blobName1 = await _blobService.UploadChunkDirectAsync(data, hash);
        var blobName2 = await _blobService.UploadChunkDirectAsync(data, hash);

        // Assert - Both return same blob name, but both actually uploaded (no dedup check)
        Assert.Equal(blobName1, blobName2);
        // TotalOperations should be 2 (both uploads counted)
        Assert.Equal(2, _blobService.TotalOperations);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_VsUploadChunkAsync_DedupBehaviorDiffers()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act - Upload with regular method first (establishes blob)
        await _blobService.UploadChunkAsync(data, hash);
        var opsAfterFirst = _blobService.TotalOperations;

        // Upload again with regular method (should trigger dedup with verification)
        await _blobService.UploadChunkAsync(data, hash);
        var opsAfterSecondRegular = _blobService.TotalOperations;

        // Upload with direct method (should upload regardless)
        await _blobService.UploadChunkDirectAsync(data, hash);
        var opsAfterDirect = _blobService.TotalOperations;

        // Assert
        // Regular upload now verifies on dedup - adds 1 operation for verification
        Assert.Equal(opsAfterFirst + 1, opsAfterSecondRegular);
        // Direct upload always uploads - increments operation count
        Assert.Equal(opsAfterSecondRegular + 1, opsAfterDirect);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_DataCanBeDownloaded()
    {
        // Arrange
        var originalData = CreateRandomContent(1024);
        var hash = ComputeHash(originalData);

        // Act
        var blobName = await _blobService.UploadChunkDirectAsync(originalData, hash);
        var downloadedData = await _blobService.DownloadChunkAsync(blobName);

        // Assert
        Assert.Equal(originalData, downloadedData);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        long reportedBytes = 0;
        SynchronousProgress<long> progress = new(b => reportedBytes = b);

        // Act
        await _blobService.UploadChunkDirectAsync(data, hash, progress: progress);

        // Assert
        Assert.True(reportedBytes > 0);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_UpdatesTotalBytesUploaded()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var initialBytes = _blobService.TotalBytesUploaded;

        // Act
        await _blobService.UploadChunkDirectAsync(data, hash);

        // Assert
        Assert.True(_blobService.TotalBytesUploaded > initialBytes);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_InvalidHashFormat_ThrowsSecurityPolicyException()
    {
        // Arrange
        var data = CreateRandomContent(1024);

        // Act & Assert - Invalid hash formats
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkDirectAsync(data, "invalid-hash"));
        
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkDirectAsync(data, "tooshort"));
    }

    #endregion

    #region Download Tests

    [Fact]
    public async Task DownloadChunkAsync_ExistingChunk_ReturnsOriginalData()
    {
        // Arrange
        var originalData = CreateRandomContent(1024);
        var hash = ComputeHash(originalData);
        var blobName = await _blobService.UploadChunkAsync(originalData, hash);

        // Act
        var downloadedData = await _blobService.DownloadChunkAsync(blobName);

        // Assert
        Assert.Equal(originalData, downloadedData);
    }

    [Fact]
    public async Task DownloadChunkAsync_NonExistentChunk_ThrowsDataIntegrityException()
    {
        // Arrange - Need a valid hash format but non-existent chunk
        var validHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        
        // Act & Assert
        await Assert.ThrowsAsync<AzureBackup.Core.DataIntegrityException>(() =>
            _blobService.DownloadChunkAsync($"chunks/{validHash}"));
    }

    [Fact]
    public async Task DownloadChunkAsync_InvalidBlobNameFormat_ThrowsSecurityPolicyException()
    {
        // Act & Assert - blob name must start with "chunks/"
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.DownloadChunkAsync("invalid/path"));
    }

    [Fact]
    public async Task DownloadChunkAsync_EmptyBlobName_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _blobService.DownloadChunkAsync(""));
    }

    #endregion

    #region Upload Validation Tests

    [Fact]
    public async Task UploadChunkAsync_InvalidHashFormat_ThrowsSecurityPolicyException()
    {
        // Arrange
        var data = CreateRandomContent(1024);

        // Act & Assert - Invalid hash formats
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkAsync(data, "invalid-hash"));
        
        await Assert.ThrowsAsync<AzureBackup.Core.SecurityPolicyException>(() =>
            _blobService.UploadChunkAsync(data, "tooshort"));
    }

    #endregion

    #region Metadata Tests

    [Fact]
    public async Task UploadFileMetadataAsync_ValidFile_Stores()
    {
        // Arrange
        var file = CreateBackedUpFile(@"C:\test.txt", 1024);

        // Act
        await _blobService.UploadFileMetadataAsync(file);

        // Assert
        var metadataBlobs = await _blobService.ListMetadataBlobsAsync();
        Assert.Single(metadataBlobs);
    }

    [Fact]
    public async Task UploadFileMetadataAsync_NullFile_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _blobService.UploadFileMetadataAsync(null!));
    }

    [Fact]
    public async Task DownloadFileMetadataAsync_ExistingMetadata_ReturnsFile()
    {
        // Arrange
        var originalFile = CreateBackedUpFile(@"C:\test.txt", 1024);
        await _blobService.UploadFileMetadataAsync(originalFile);
        
        var metadataBlobs = await _blobService.ListMetadataBlobsAsync();
        var blobName = metadataBlobs.First();

        // Act
        var downloadedFile = await _blobService.DownloadFileMetadataAsync(blobName);

        // Assert
        Assert.NotNull(downloadedFile);
        Assert.Equal(originalFile.LocalPath, downloadedFile.LocalPath);
        Assert.Equal(originalFile.FileSize, downloadedFile.FileSize);
    }

    [Fact]
    public async Task ListMetadataBlobsAsync_MultipleFiles_ReturnsAll()
    {
        // Arrange
        var files = new[]
        {
            CreateBackedUpFile(@"C:\file1.txt", 1024),
            CreateBackedUpFile(@"C:\file2.txt", 2048),
            CreateBackedUpFile(@"C:\file3.txt", 4096)
        };

        foreach (var file in files)
        {
            await _blobService.UploadFileMetadataAsync(file);
        }

        // Act
        var blobs = await _blobService.ListMetadataBlobsAsync();

        // Assert
        Assert.Equal(3, blobs.Count);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task DeleteBlobAsync_ExistingBlob_RemovesBlob()
    {
        // Arrange
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var blobName = await _blobService.UploadChunkAsync(data, hash);

        // Act
        await _blobService.DeleteBlobAsync(blobName);

        // Assert
        Assert.DoesNotContain(blobName, _blobService.StoredBlobNames);
    }

    [Fact]
    public async Task DeleteBlobAsync_NonExistentBlob_DoesNotThrow()
    {
        // Act - Should not throw
        await _blobService.DeleteBlobAsync("chunks/nonexistent");
    }

    #endregion

    #region Latency and Cancellation Tests

    [Fact]
    public async Task BlobService_WithLatency_StillCompletes()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 50);
        await blobService.ConnectAsync("conn", "container");
        
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var blobName = await blobService.UploadChunkAsync(data, hash);
        var downloaded = await blobService.DownloadChunkAsync(blobName);

        stopwatch.Stop();

        // Assert
        Assert.Equal(data, downloaded);
        Assert.True(stopwatch.ElapsedMilliseconds >= 80); // Should take ~100ms (2 x 50ms latency)
    }

    [Fact]
    public async Task UploadChunkAsync_CancellationDuringDelay_ThrowsOperationCancelled()
    {
        // Arrange - Use long latency so cancellation triggers during delay
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 5000);
        await blobService.ConnectAsync("conn", "container");
        
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        CancellationTokenSource cts = new();

        // Act - Start upload and cancel during the delay
        var uploadTask = blobService.UploadChunkAsync(data, hash, cancellationToken: cts.Token);
        cts.CancelAfter(50); // Cancel after 50ms (during the 5000ms delay)

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => uploadTask);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private static string ComputeHash(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private static BackedUpFile CreateBackedUpFile(string path, long size)
    {
        return new BackedUpFile
        {
            LocalPath = path,
            BlobName = $"files/{Guid.NewGuid()}",
            FileSize = size,
            LastModified = DateTime.UtcNow,
            FileHash = Guid.NewGuid().ToString("N"),
            Chunks = [new ChunkInfo { Index = 0, Offset = 0, Length = (int)size, Hash = "abc123" }],
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        };
    }

    #endregion
}
