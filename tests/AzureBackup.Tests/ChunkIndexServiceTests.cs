using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for ChunkIndexService.
/// </summary>
public class ChunkIndexServiceTests : IAsyncLifetime
{
    private string _testDbPath = null!;
    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private ChunkIndexService _indexService = null!;

    public async Task InitializeAsync()
    {
        // Create unique test database path
        _testDbPath = Path.Combine(Path.GetTempPath(), $"ChunkIndexTest_{Guid.NewGuid()}.db");
        
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_testDbPath);
        
        _encryptionService = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync("TestPassword123!", salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
        
        _blobService = new InMemoryBlobService(_encryptionService);
        await _blobService.ConnectAsync("test-connection", "test-container");
        
        _indexService = new ChunkIndexService(_databaseService, _blobService);
    }

    public Task DisposeAsync()
    {
        _encryptionService?.Dispose();
        _databaseService?.Dispose();
        try
        {
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);
        }
        catch { /* Ignore cleanup errors */ }
        return Task.CompletedTask;
    }

    #region Reference Management Tests

    [Fact]
    public void AddReference_CreatesNewChunkEntry_WhenChunkDoesNotExist()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath = @"C:\TestFiles\file1.txt";
        
        // Act
        _indexService.AddReference(chunkHash, filePath, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: true);
        
        // Assert
        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        Assert.NotNull(entry);
        Assert.Equal(chunkHash, entry.ChunkHash);
        Assert.Equal(1, entry.ReferenceCount);
        Assert.Single(entry.ReferencingFiles);
        Assert.Equal(filePath, entry.ReferencingFiles[0].FilePath);
        Assert.Equal(1024, entry.SizeBytes);
        Assert.Equal(StorageTier.Cool, entry.CurrentTier);
    }

    [Fact]
    public void AddReference_IncrementsReferenceCount_WhenChunkAlreadyExists()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath1 = @"C:\TestFiles\file1.txt";
        var filePath2 = @"C:\TestFiles\file2.txt";
        
        _indexService.AddReference(chunkHash, filePath1, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: true);
        
        // Act
        _indexService.AddReference(chunkHash, filePath2, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: false);
        
        // Assert
        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        Assert.NotNull(entry);
        Assert.Equal(2, entry.ReferenceCount);
        Assert.Equal(2, entry.ReferencingFiles.Count);
    }

    [Fact]
    public void AddReference_DoesNotDuplicate_WhenSameFileAddsReference()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath = @"C:\TestFiles\file1.txt";
        
        _indexService.AddReference(chunkHash, filePath, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: true);
        
        // Act - try to add same file again
        _indexService.AddReference(chunkHash, filePath, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: false);
        
        // Assert
        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.ReferenceCount);
        Assert.Single(entry.ReferencingFiles);
    }

    [Fact]
    public async Task RemoveFileReferencesAsync_DecrementsReferenceCount()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath1 = @"C:\TestFiles\file1.txt";
        var filePath2 = @"C:\TestFiles\file2.txt";
        
        // Upload chunk so it exists in blob storage
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkAsync(chunkData, chunkHash, StorageTier.Cool);
        
        _indexService.AddReference(chunkHash, filePath1, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: false);
        _indexService.AddReference(chunkHash, filePath2, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: false);
        
        // Act
        await _indexService.RemoveFileReferencesAsync(filePath1);
        
        // Assert
        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.ReferenceCount);
        Assert.Single(entry.ReferencingFiles);
        Assert.Equal(filePath2, entry.ReferencingFiles[0].FilePath);
    }

    [Fact]
    public async Task RemoveFileReferencesAsync_DeletesChunk_WhenReferenceCountReachesZero()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath = @"C:\TestFiles\file1.txt";
        
        // Upload chunk so it exists in blob storage
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkAsync(chunkData, chunkHash, StorageTier.Cool);
        
        _indexService.AddReference(chunkHash, filePath, chunkIndex: 0, sizeBytes: 1024, 
            StorageTier.Cool, isNewChunk: false);
        
        // Act
        var deletedCount = await _indexService.RemoveFileReferencesAsync(filePath);
        
        // Assert
        Assert.Equal(1, deletedCount);
        Assert.Null(_databaseService.GetChunkIndexEntry(chunkHash));
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetIndexSummary_ReturnsCorrectStatistics()
    {
        // Arrange - Add some chunks with different states
        var hash1 = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var hash2 = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
        var hash3 = "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        
        // Chunk 1: Referenced by 2 files (dedup)
        _indexService.AddReference(hash1, @"C:\file1.txt", 0, 1000, StorageTier.Hot, true);
        _indexService.AddReference(hash1, @"C:\file2.txt", 0, 1000, StorageTier.Hot, false);
        
        // Chunk 2: Referenced by 1 file
        _indexService.AddReference(hash2, @"C:\file1.txt", 1, 2000, StorageTier.Cool, true);
        
        // Chunk 3: Orphaned (0 references) - add then remove
        _indexService.AddReference(hash3, @"C:\temp.txt", 0, 500, StorageTier.Cold, true);
        var entry = _databaseService.GetChunkIndexEntry(hash3)!;
        entry.ReferencingFiles.Clear();
        entry.ReferenceCount = 0;
        _databaseService.SaveChunkIndexEntry(entry);
        
        // Act
        var summary = _indexService.GetIndexSummary();
        
        // Assert
        Assert.Equal(3, summary.TotalChunks);
        Assert.Equal(3500, summary.TotalSizeBytes);
        Assert.Equal(1, summary.OrphanCount);
        Assert.Equal(500, summary.OrphanSizeBytes);
        Assert.Equal(1, summary.SharedChunks);
        Assert.Equal(1000, summary.DeduplicationSavingsBytes); // hash1 saved 1000 bytes
        
        // Tier breakdown
        Assert.True(summary.TierBreakdown.ContainsKey(StorageTier.Hot));
        Assert.Equal(1, summary.TierBreakdown[StorageTier.Hot].ChunkCount);
        Assert.Equal(1000, summary.TierBreakdown[StorageTier.Hot].TotalSizeBytes);
    }

    #endregion

    #region Update File Chunks Tests

    [Fact]
    public async Task UpdateFileChunksAsync_HandlesModifiedFile()
    {
        // Arrange
        var oldHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var newHash = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
        var filePath = @"C:\TestFiles\file1.txt";
        
        // Upload old chunk so it exists
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkAsync(chunkData, oldHash, StorageTier.Cool);
        
        // Add reference to old chunk
        _indexService.AddReference(oldHash, filePath, 0, 1024, StorageTier.Cool, true);
        
        // Act - Update file with new chunk
        var oldChunks = new List<string> { oldHash };
        var newChunks = new List<(string hash, int index, long size, bool isNew)>
        {
            (newHash, 0, 2048, true)
        };
        
        await _indexService.UpdateFileChunksAsync(filePath, oldChunks, newChunks, StorageTier.Cool);
        
        // Assert
        // Old chunk should be deleted (was orphaned)
        Assert.Null(_databaseService.GetChunkIndexEntry(oldHash));
        
        // New chunk should exist with reference
        var newEntry = _databaseService.GetChunkIndexEntry(newHash);
        Assert.NotNull(newEntry);
        Assert.Equal(1, newEntry.ReferenceCount);
        Assert.Equal(filePath, newEntry.ReferencingFiles[0].FilePath);
    }

    #endregion

    #region Consistency Verification Tests

    [Fact]
    public void VerifyBackupConsistency_LogsWarning_WhenChunkMissing()
    {
        // Arrange
        var chunkHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var filePath = @"C:\TestFiles\file1.txt";
        var logMessages = new List<string>();
        _indexService.DiagnosticLog += (s, msg) => logMessages.Add(msg);
        
        // Don't add the chunk to the index
        
        // Act
        _indexService.VerifyBackupConsistency(filePath, new List<string> { chunkHash });
        
        // Assert
        Assert.Contains(logMessages, m => m.Contains("WARNING") && m.Contains("not found"));
    }

    #endregion
}
