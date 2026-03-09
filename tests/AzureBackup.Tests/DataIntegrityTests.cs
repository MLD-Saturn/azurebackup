using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for data integrity verification throughout the backup/restore pipeline.
/// Ensures that encryption, chunking, and reassembly maintain data integrity.
/// </summary>
public class DataIntegrityTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "DataIntegrityTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"IntegrityTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);
        
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath);
        
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
    }

    public Task DisposeAsync()
    {
        _encryptionService.Dispose();
        _databaseService.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
        return Task.CompletedTask;
    }

    #region Hash Verification Tests

    [Fact]
    public async Task FileHash_ComputedCorrectly_MatchesAfterRestore()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(500 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "hash_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var originalHash = await _chunkingService.ComputeFileHashAsync(sourceFile);
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        
        // Act
        var restorePath = Path.Combine(_restoreDirectory, "hash_test.bin");
        await restoreService.RestoreFileAsync(backedUp, restorePath, true);
        
        var restoredHash = await _chunkingService.ComputeFileHashAsync(restorePath);

        // Assert
        Assert.Equal(originalHash, restoredHash);
        Assert.Equal(backedUp.FileHash, restoredHash);
    }

    [Fact]
    public async Task ChunkHashes_ComputedConsistently_SameContentSameHash()
    {
        // Arrange
        var content = CreateRandomContent(200 * 1024);
        var file1 = Path.Combine(_sourceDirectory, "chunk_hash_1.bin");
        var file2 = Path.Combine(_sourceDirectory, "chunk_hash_2.bin");
        
        // Write identical content to both files
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act
        var chunks1 = await _chunkingService.ChunkFileAsync(file1);
        var chunks2 = await _chunkingService.ChunkFileAsync(file2);

        // Assert - Same content should produce same chunk hashes
        Assert.Equal(chunks1.Count, chunks2.Count);
        for (int i = 0; i < chunks1.Count; i++)
        {
            Assert.Equal(chunks1[i].Hash, chunks2[i].Hash);
        }
    }

    #endregion

    #region Chunk Integrity Tests

    [Fact]
    public async Task ChunkBoundaries_ContiguousAndComplete()
    {
        // Arrange
        var content = CreateRandomContent(3 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "boundary_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(sourceFile);

        // Assert - Chunks should be contiguous
        var sortedChunks = chunks.OrderBy(c => c.Offset).ToList();
        long expectedOffset = 0;
        
        for (int i = 0; i < sortedChunks.Count; i++)
        {
            Assert.Equal(expectedOffset, sortedChunks[i].Offset);
            Assert.True(sortedChunks[i].Length > 0);
            expectedOffset += sortedChunks[i].Length;
        }
        
        // Total should equal file size
        Assert.Equal(content.Length, expectedOffset);
    }

    [Fact]
    public async Task ChunkIndices_SequentialAndZeroBased()
    {
        // Arrange
        var content = CreateRandomContent(2 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "index_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(sourceFile);

        // Assert
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
        }
    }

    [Fact]
    public async Task ChunkData_ReadCorrectly_MatchesOriginal()
    {
        // Arrange
        var content = CreateRandomContent(500 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "chunk_read_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);

        var chunks = await _chunkingService.ChunkFileAsync(sourceFile);

        // Act & Assert - Read each chunk and verify it matches the original data
        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(sourceFile, chunk);
            
            // Verify length
            Assert.Equal(chunk.Length, chunkData.Length);
            
            // Verify content matches original at that offset
            var originalSegment = content.AsSpan((int)chunk.Offset, chunk.Length);
            Assert.True(originalSegment.SequenceEqual(chunkData));
        }
    }

    #endregion

    #region Encryption Integrity Tests

    [Fact]
    public async Task EncryptedChunk_DecryptsToOriginal()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");

        var originalData = CreateRandomContent(64 * 1024);
        var hash = ComputeHash(originalData);

        // Act - Upload (encrypts) and download (decrypts)
        var blobName = await blobService.UploadChunkAsync(originalData, hash);
        var decryptedData = await blobService.DownloadChunkAsync(blobName);

        // Assert
        Assert.Equal(originalData, decryptedData);
    }

    [Fact]
    public void EncryptedData_DifferentFromOriginal()
    {
        // Arrange - This test verifies encryption is actually happening
        var originalData = CreateRandomContent(1024);
        
        // Act
        var encryptedData = _encryptionService.Encrypt(originalData);
        
        // Assert - Encrypted data should be different from original
        Assert.NotEqual(originalData, encryptedData);
        
        // Encrypted data should be larger (due to IV, tag, etc.)
        Assert.True(encryptedData.Length > originalData.Length);
    }

    [Fact]
    public async Task DifferentEncryptionCalls_ProduceDifferentCiphertext()
    {
        // Arrange - AES-GCM should produce different ciphertext each time due to random IV
        var originalData = CreateRandomContent(1024);
        
        // Act
        var encrypted1 = _encryptionService.Encrypt(originalData);
        var encrypted2 = _encryptionService.Encrypt(originalData);
        
        // Assert - Same plaintext should produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);
        
        // But both should decrypt to the same original
        var decrypted1 = _encryptionService.Decrypt(encrypted1);
        var decrypted2 = _encryptionService.Decrypt(encrypted2);
        Assert.Equal(originalData, decrypted1);
        Assert.Equal(originalData, decrypted2);

        await Task.CompletedTask;
    }

    #endregion

    #region Restore Integrity Verification Tests

    [Fact]
    public async Task RestoreService_VerifiesFileHash_AfterRestore()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(500 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "verify_hash.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "verify_hash.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        // The restore service should have verified the hash internally
        // If it didn't match, it would have thrown DataIntegrityException
        var restoredHash = await _chunkingService.ComputeFileHashAsync(restorePath);
        Assert.Equal(backedUp.FileHash, restoredHash);
    }

    [Fact]
    public async Task RestoreService_CorruptedChunk_ThrowsDataIntegrityException()
    {
        // Arrange
        CorruptingBlobService corruptingService = new(_encryptionService);
        await corruptingService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, corruptingService, _encryptionService);

        var content = CreateRandomContent(100 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "corrupt_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(corruptingService, sourceFile);
        
        // Enable corruption for downloads
        corruptingService.CorruptDownloads = true;
        
        var restorePath = Path.Combine(_restoreDirectory, "corrupt_test.bin");

        // Act & Assert
        await Assert.ThrowsAsync<DataIntegrityException>(() =>
            restoreService.RestoreFileAsync(backedUp, restorePath, true));
    }

    [Fact]
    public async Task RestoreService_ChunkSizeMismatch_ThrowsDataIntegrityException()
    {
        // Arrange
        TruncatingBlobService truncatingService = new(_encryptionService);
        await truncatingService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, truncatingService, _encryptionService);

        var content = CreateRandomContent(100 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "truncate_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(truncatingService, sourceFile);
        
        // Enable truncation for downloads
        truncatingService.TruncateDownloads = true;
        
        var restorePath = Path.Combine(_restoreDirectory, "truncate_test.bin");

        // Act & Assert
        await Assert.ThrowsAsync<DataIntegrityException>(() =>
            restoreService.RestoreFileAsync(backedUp, restorePath, true));
    }

    #endregion

    #region Security Validation Tests

    [Fact]
    public async Task BlobService_InvalidChunkHash_ThrowsSecurityPolicyException()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        
        var data = CreateRandomContent(1024);

        // Act & Assert - Invalid hash format
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            blobService.UploadChunkAsync(data, "invalid-hash"));
        
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            blobService.UploadChunkAsync(data, "tooshort"));
        
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            blobService.UploadChunkAsync(data, ""));
    }

    [Fact]
    public async Task BlobService_InvalidBlobNameFormat_ThrowsSecurityPolicyException()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");

        // Act & Assert - Blob name must start with "chunks/"
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            blobService.DownloadChunkAsync("invalid/path"));
        
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            blobService.DownloadChunkAsync("../../../etc/passwd"));
    }

    #endregion

    #region Helper Methods

    private async Task<BackedUpFile> BackupFileAsync(IBlobStorageService blobService, string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);
        var fileHash = await _chunkingService.ComputeFileHashAsync(filePath);

        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk);
            chunk.BlobName = await blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        BackedUpFile backedUp = new()
        {
            LocalPath = filePath,
            BlobName = $"files/{Guid.NewGuid()}",
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            FileHash = fileHash,
            Chunks = chunks,
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        };

        await blobService.UploadFileMetadataAsync(backedUp);
        _databaseService.SaveBackedUpFile(backedUp);

        return backedUp;
    }

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

    #endregion
}

/// <summary>
/// Test double that corrupts downloaded data.
/// </summary>
internal class CorruptingBlobService : InMemoryBlobService
{
    public bool CorruptDownloads { get; set; }

    public CorruptingBlobService(EncryptionService encryptionService) 
        : base(encryptionService)
    {
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);
        
        if (CorruptDownloads && data.Length > 0)
        {
            // Corrupt one byte - this will cause hash mismatch
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }
        
        return data;
    }
}

/// <summary>
/// Test double that truncates downloaded data.
/// </summary>
internal class TruncatingBlobService : InMemoryBlobService
{
    public bool TruncateDownloads { get; set; }

    public TruncatingBlobService(EncryptionService encryptionService) 
        : base(encryptionService)
    {
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);
        
        if (TruncateDownloads && data.Length > 10)
        {
            // Return truncated data - this will cause size mismatch
            return data[..^10];
        }
        
        return data;
    }
}
