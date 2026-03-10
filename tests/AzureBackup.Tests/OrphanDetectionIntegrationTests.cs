using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Integration tests for orphan detection and cleanup functionality.
/// </summary>
public class OrphanDetectionIntegrationTests : IAsyncLifetime
{
    private string _testDbPath = null!;
    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private ChunkIndexService _indexService = null!;

    public async Task InitializeAsync()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"OrphanTest_{Guid.NewGuid()}.db");
        
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

    [Fact]
    public async Task ScanForOrphansAsync_FindsChunksNotInIndex()
    {
        // Arrange - Upload a chunk directly without adding to index
        var orphanHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, orphanHash, StorageTier.Cool);
        
        // Also add a properly tracked chunk
        var trackedHash = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
        await _blobService.UploadChunkDirectAsync(chunkData, trackedHash, StorageTier.Cool);
        _indexService.AddReference(trackedHash, @"C:\file.txt", 0, 1024, StorageTier.Cool, true);
        
        // Act
        var result = await _indexService.ScanForOrphansAsync();
        
        // Assert
        Assert.Equal(1, result.OrphanedChunks.Count);
        Assert.Contains(result.OrphanedChunks, o => o.ChunkHash == orphanHash);
    }

    [Fact]
    public async Task CleanupOrphansAsync_DeletesOrphanedChunks()
    {
        // Arrange - Upload an orphan chunk
        var orphanHash = "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        var chunkData = new byte[2048];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, orphanHash, StorageTier.Cool);
        
        // Create an orphan entry
        var orphanEntry = new ChunkIndexEntry
        {
            ChunkHash = orphanHash,
            SizeBytes = 2048,
            CurrentTier = StorageTier.Cool,
            ReferenceCount = 0,
            ReferencingFiles = []
        };
        
        // Act
        var result = await _indexService.CleanupOrphansAsync(new[] { orphanEntry });
        
        // Assert
        Assert.Equal(1, result.ChunksDeleted);
        Assert.Equal(2048, result.BytesFreed);
        Assert.Equal(0, result.FailedDeletions);
        
        // Verify chunk is actually deleted
        var exists = await _blobService.BlobExistsAsync($"chunks/{orphanHash}");
        Assert.False(exists);
    }

    [Fact]
    public async Task FullOrphanDetectionWorkflow_EndToEnd()
    {
        // Arrange - Simulate a file backup then deletion scenario
        var hash1 = "d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5";
        var hash2 = "e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6";
        var filePath = @"C:\TestFiles\document.txt";
        
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        
        // Upload chunks and track in index (simulating backup)
        await _blobService.UploadChunkDirectAsync(chunkData, hash1, StorageTier.Cool);
        await _blobService.UploadChunkDirectAsync(chunkData, hash2, StorageTier.Cool);
        _indexService.AddReference(hash1, filePath, 0, 1024, StorageTier.Cool, true);
        _indexService.AddReference(hash2, filePath, 1, 1024, StorageTier.Cool, true);
        
        // Verify no orphans initially
        var initialScan = await _indexService.ScanForOrphansAsync();
        Assert.Empty(initialScan.OrphanedChunks);
        
        // Act - Remove file references (simulating file deletion)
        await _indexService.RemoveFileReferencesAsync(filePath);
        
        // Verify chunks were deleted automatically (since ref count = 0)
        var exists1 = await _blobService.BlobExistsAsync($"chunks/{hash1}");
        var exists2 = await _blobService.BlobExistsAsync($"chunks/{hash2}");
        Assert.False(exists1);
        Assert.False(exists2);
        
        // Also verify index entries are removed
        Assert.Null(_databaseService.GetChunkIndexEntry(hash1));
        Assert.Null(_databaseService.GetChunkIndexEntry(hash2));
    }

    [Fact]
    public async Task SharedChunks_NotDeletedWhenOneFileRemoved()
    {
        // Arrange - Two files sharing a chunk
        var sharedHash = "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1";
        var file1 = @"C:\file1.txt";
        var file2 = @"C:\file2.txt";
        
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, sharedHash, StorageTier.Cool);
        
        // Both files reference the same chunk
        _indexService.AddReference(sharedHash, file1, 0, 1024, StorageTier.Cool, true);
        _indexService.AddReference(sharedHash, file2, 0, 1024, StorageTier.Cool, false);
        
        // Act - Remove one file's references
        var deleted = await _indexService.RemoveFileReferencesAsync(file1);
        
        // Assert - Chunk should NOT be deleted (still referenced by file2)
        Assert.Equal(0, deleted);
        var entry = _databaseService.GetChunkIndexEntry(sharedHash);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.ReferenceCount);
        
        var exists = await _blobService.BlobExistsAsync($"chunks/{sharedHash}");
        Assert.True(exists);
    }

    [Fact]
    public void GetIndexSummary_ReflectsOrphanStatistics()
    {
        // Arrange - Create entries with various states
        var hash1 = "a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2";
        var hash2 = "b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2";
        
        // Normal chunk with reference
        _indexService.AddReference(hash1, @"C:\file.txt", 0, 1000, StorageTier.Hot, true);
        
        // Orphan chunk (manually create with 0 refs)
        var orphanEntry = new ChunkIndexEntry
        {
            ChunkHash = hash2,
            SizeBytes = 500,
            CurrentTier = StorageTier.Cold,
            ReferenceCount = 0,
            ReferencingFiles = [],
            FirstUploadedAt = DateTime.UtcNow
        };
        _databaseService.SaveChunkIndexEntry(orphanEntry);
        
        // Act
        var summary = _indexService.GetIndexSummary();
        
        // Assert
        Assert.Equal(2, summary.TotalChunks);
        Assert.Equal(1500, summary.TotalSizeBytes);
        Assert.Equal(1, summary.OrphanCount);
        Assert.Equal(500, summary.OrphanSizeBytes);
        Assert.Contains(StorageTier.Hot, summary.TierBreakdown.Keys);
        Assert.Contains(StorageTier.Cold, summary.TierBreakdown.Keys);
    }
}
