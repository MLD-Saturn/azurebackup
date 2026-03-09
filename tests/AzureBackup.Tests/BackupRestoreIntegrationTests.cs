using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Integration tests that test the full backup and restore flow
/// using the InMemoryBlobService instead of real Azure storage.
/// </summary>
public class BackupRestoreIntegrationTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "IntegrationTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"IntegrationTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);
        
        // Initialize services
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath);
        
        // Initialize encryption
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
        
        // Connect blob service using connection string (simpler for testing)
        await _blobService.ConnectAsync("fake-connection-string", "test-container");
    }

    public Task DisposeAsync()
    {
        _encryptionService.Dispose();
        _databaseService.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    #region Full Backup/Restore Flow Tests

    [Fact]
    public async Task BackupAndRestore_SingleFile_PreservesContent()
    {
        // Arrange
        var originalContent = CreateRandomContent(100 * 1024); // 100 KB
        var sourceFile = Path.Combine(_sourceDirectory, "document.txt");
        await File.WriteAllBytesAsync(sourceFile, originalContent);

        // Act - Backup
        var backedUpFile = await BackupFileAsync(sourceFile);
        
        // Act - Restore to different location
        var restorePath = Path.Combine(_restoreDirectory, "document.txt");
        await RestoreFileAsync(backedUpFile, restorePath);

        // Assert
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(originalContent, restoredContent);
    }

    [Fact]
    public async Task BackupAndRestore_LargeFile_PreservesContent()
    {
        // Arrange - Create 5 MB file
        var originalContent = CreateRandomContent(5 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "large.bin");
        await File.WriteAllBytesAsync(sourceFile, originalContent);

        // Act - Backup
        var backedUpFile = await BackupFileAsync(sourceFile);
        
        // Verify chunking occurred
        Assert.True(backedUpFile.Chunks.Count > 1);
        
        // Act - Restore
        var restorePath = Path.Combine(_restoreDirectory, "large.bin");
        await RestoreFileAsync(backedUpFile, restorePath);

        // Assert
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(originalContent, restoredContent);
    }

    [Fact]
    public async Task BackupAndRestore_MultipleFiles_AllRestoreCorrectly()
    {
        // Arrange - Create multiple files
        Dictionary<string, byte[]> files = new();
        for (int i = 0; i < 5; i++)
        {
            var content = CreateRandomContent(50 * 1024 + i * 10000);
            var filename = $"file{i}.txt";
            files[filename] = content;
            await File.WriteAllBytesAsync(Path.Combine(_sourceDirectory, filename), content);
        }

        // Act - Backup all files
        List<BackedUpFile> backedUpFiles = new();
        foreach (var filename in files.Keys)
        {
            var sourcePath = Path.Combine(_sourceDirectory, filename);
            var backedUp = await BackupFileAsync(sourcePath);
            backedUpFiles.Add(backedUp);
        }

        // Act - Restore all files
        foreach (var backedUp in backedUpFiles)
        {
            var restorePath = Path.Combine(_restoreDirectory, Path.GetFileName(backedUp.LocalPath));
            await RestoreFileAsync(backedUp, restorePath);
        }

        // Assert - All files match
        foreach (var (filename, originalContent) in files)
        {
            var restoredContent = await File.ReadAllBytesAsync(Path.Combine(_restoreDirectory, filename));
            Assert.Equal(originalContent, restoredContent);
        }
    }

    #endregion

    #region Deduplication Tests

    [Fact]
    public async Task Backup_DuplicateFiles_DeduplicatesChunks()
    {
        // Arrange - Create two identical files
        var content = CreateRandomContent(256 * 1024);
        var file1 = Path.Combine(_sourceDirectory, "copy1.bin");
        var file2 = Path.Combine(_sourceDirectory, "copy2.bin");
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act
        var backedUp1 = await BackupFileAsync(file1);
        var chunkBlobCountAfterFirst = _blobService.StoredBlobNames.Count(n => n.StartsWith("chunks/"));
        
        var backedUp2 = await BackupFileAsync(file2);
        var chunkBlobCountAfterSecond = _blobService.StoredBlobNames.Count(n => n.StartsWith("chunks/"));

        // Assert - No new chunk blobs should be added (deduplication)
        Assert.Equal(chunkBlobCountAfterFirst, chunkBlobCountAfterSecond);
        Assert.Equal(backedUp1.Chunks.Select(c => c.Hash), backedUp2.Chunks.Select(c => c.Hash));
    }

    [Fact]
    public async Task Backup_ModifiedFile_OnlyUploadsChangedChunks()
    {
        // Arrange - Create initial file
        var content = CreateRandomContent(500 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "modified.bin");
        await File.WriteAllBytesAsync(filePath, content);
        
        var initialBackup = await BackupFileAsync(filePath);
        var initialBlobCount = _blobService.StoredBlobNames.Count;
        var initialBytesUploaded = _blobService.TotalBytesUploaded;

        // Modify small portion at the end
        content[^1000..].AsSpan().Fill(0xFF);
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var modifiedBackup = await BackupFileAsync(filePath);
        var newBlobCount = _blobService.StoredBlobNames.Count;
        var additionalBytesUploaded = _blobService.TotalBytesUploaded - initialBytesUploaded;

        // Assert - Should upload less than full file size
        Assert.True(additionalBytesUploaded < content.Length);
    }

    #endregion

    #region Metadata Tests

    [Fact]
    public async Task Backup_StoresMetadataInBlob()
    {
        // Arrange
        var sourceFile = Path.Combine(_sourceDirectory, "metadata_test.txt");
        await File.WriteAllBytesAsync(sourceFile, CreateRandomContent(1024));

        // Act
        var backedUp = await BackupFileAsync(sourceFile);
        await _blobService.UploadFileMetadataAsync(backedUp);

        // Assert
        var metadataBlobs = await _blobService.ListMetadataBlobsAsync();
        Assert.Single(metadataBlobs);
        
        var retrieved = await _blobService.DownloadFileMetadataAsync(metadataBlobs[0]);
        Assert.NotNull(retrieved);
        Assert.Equal(backedUp.LocalPath, retrieved.LocalPath);
        Assert.Equal(backedUp.FileHash, retrieved.FileHash);
    }

    [Fact]
    public async Task Backup_MetadataIncludesChunkInfo()
    {
        // Arrange
        var sourceFile = Path.Combine(_sourceDirectory, "chunk_metadata.bin");
        await File.WriteAllBytesAsync(sourceFile, CreateRandomContent(300 * 1024));

        // Act
        var backedUp = await BackupFileAsync(sourceFile);
        await _blobService.UploadFileMetadataAsync(backedUp);
        
        var metadataBlobs = await _blobService.ListMetadataBlobsAsync();
        var retrieved = await _blobService.DownloadFileMetadataAsync(metadataBlobs[0]);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(backedUp.Chunks.Count, retrieved.Chunks.Count);
        for (int i = 0; i < backedUp.Chunks.Count; i++)
        {
            Assert.Equal(backedUp.Chunks[i].Hash, retrieved.Chunks[i].Hash);
            Assert.Equal(backedUp.Chunks[i].Length, retrieved.Chunks[i].Length);
        }
    }

    #endregion

    #region Integrity Tests

    [Fact]
    public async Task Restore_VerifiesFileHash()
    {
        // Arrange
        var content = CreateRandomContent(100 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "verify_hash.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(sourceFile);

        // Act
        var restorePath = Path.Combine(_restoreDirectory, "verify_hash.txt");
        await RestoreFileAsync(backedUp, restorePath);

        // Assert - Compute hash of restored file and compare
        var restoredHash = await _chunkingService.ComputeFileHashAsync(restorePath);
        Assert.Equal(backedUp.FileHash, restoredHash);
    }

    [Fact]
    public async Task Restore_AllChunksOrdered_AssemblesCorrectly()
    {
        // Arrange - Large file with multiple chunks
        var content = CreateRandomContent(2 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "ordered.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(sourceFile);
        
        // Verify multiple chunks
        Assert.True(backedUp.Chunks.Count > 1);

        // Act
        var restorePath = Path.Combine(_restoreDirectory, "ordered.bin");
        await RestoreFileAsync(backedUp, restorePath);

        // Assert
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task Restore_MissingChunk_ThrowsDataIntegrityException()
    {
        // Arrange
        var content = CreateRandomContent(100 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "missing_chunk.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(sourceFile);
        
        // Delete a chunk from blob storage
        await _blobService.DeleteBlobAsync(backedUp.Chunks[0].BlobName);

        // Act & Assert
        var restorePath = Path.Combine(_restoreDirectory, "missing_chunk.txt");
        await Assert.ThrowsAsync<AzureBackup.Core.DataIntegrityException>(() => 
            RestoreFileAsync(backedUp, restorePath));
    }

    #endregion

    #region Encryption Tests

    [Fact]
    public async Task Backup_DataIsEncryptedInStorage()
    {
        // Arrange
        var content = "This is sensitive data that should be encrypted"u8.ToArray();
        var sourceFile = Path.Combine(_sourceDirectory, "sensitive.txt");
        await File.WriteAllBytesAsync(sourceFile, content);

        // Act
        var backedUp = await BackupFileAsync(sourceFile);

        // Assert - Raw blob data should not contain plaintext
        var rawBlob = _blobService.GetRawBlob(backedUp.Chunks[0].BlobName);
        Assert.NotNull(rawBlob);
        
        var rawString = System.Text.Encoding.UTF8.GetString(rawBlob);
        Assert.DoesNotContain("sensitive", rawString);
    }

    #endregion

    #region Helper Methods

    private async Task<BackedUpFile> BackupFileAsync(string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var fileHash = await _chunkingService.ComputeFileHashAsync(filePath);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Upload each chunk
        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk);
            chunk.BlobName = await _blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        return new BackedUpFile
        {
            LocalPath = filePath,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            FileHash = fileHash,
            Chunks = chunks,
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        };
    }

    private async Task RestoreFileAsync(BackedUpFile file, string restorePath)
    {
        // Create directory if needed
        var directory = Path.GetDirectoryName(restorePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Download and reassemble chunks
        await using FileStream outputStream = new(restorePath, FileMode.Create);
        
        foreach (var chunk in file.Chunks.OrderBy(c => c.Index))
        {
            var chunkData = await _blobService.DownloadChunkAsync(chunk.BlobName);
            await outputStream.WriteAsync(chunkData);
        }
    }

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    #endregion
}
