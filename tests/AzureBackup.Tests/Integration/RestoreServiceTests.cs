using AzureBackup.Tests.Infrastructure;
using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for RestoreService functionality including delete operations.
/// </summary>
public class RestoreServiceTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private RestoreService _restoreService = null!;

    private const string TestPassword = "TestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestoreServiceTests_{Guid.NewGuid():N}");
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
        _databaseService.Initialize(_dbPath, TestPassword);
        _restoreService = new RestoreService(_databaseService, _blobService, _encryptionService);
        
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
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
        return Task.CompletedTask;
    }

    #region ListRestorableFilesAsync Tests

    [Fact]
    public async Task ListRestorableFilesAsync_ReturnsAllBackedUpFiles()
    {
        // Arrange
        await CreateAndBackupFile("file1.txt", 1024);
        await CreateAndBackupFile("file2.txt", 2048);
        await CreateAndBackupFile("file3.txt", 512);

        // Act
        var files = await _restoreService.ListRestorableFilesAsync();

        // Assert
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public async Task ListRestorableFilesAsync_ReturnsEmptyWhenNoFiles()
    {
        // Act
        var files = await _restoreService.ListRestorableFilesAsync();

        // Assert
        Assert.Empty(files);
    }

    #endregion

    #region MirrorSyncToLocalAsync Tests

    [Fact]
    public async Task MirrorSyncToLocalAsync_RestoresNewFiles()
    {
        // Arrange
        await CreateAndBackupFile("test.txt", 1024);
        var targetDir = Path.Combine(_testDirectory, "mirror_target");
        Directory.CreateDirectory(targetDir);
        
        var files = await _restoreService.ListRestorableFilesAsync();

        // Act
        var result = await _restoreService.MirrorSyncToLocalAsync(
            files, targetDir, _sourceDirectory);

        // Assert
        Assert.Equal(1, result.FilesTransferred);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.FilesUnchanged);
        Assert.True(File.Exists(Path.Combine(targetDir, "test.txt")));
    }

    [Fact]
    public async Task MirrorSyncToLocalAsync_SkipsUnchangedFiles()
    {
        // Arrange
        var targetDir = Path.Combine(_testDirectory, "mirror_target");
        Directory.CreateDirectory(targetDir);
        
        await CreateAndBackupFile("test.txt", 1024);
        var files = await _restoreService.ListRestorableFilesAsync();
        
        // First sync
        await _restoreService.MirrorSyncToLocalAsync(files, targetDir, _sourceDirectory);

        // Act - Second sync
        var result = await _restoreService.MirrorSyncToLocalAsync(files, targetDir, _sourceDirectory);

        // Assert
        Assert.Equal(0, result.FilesTransferred);
        Assert.Equal(1, result.FilesUnchanged);
    }

    [Fact]
    public async Task MirrorSyncToLocalAsync_DeletesExtraLocalFiles()
    {
        // Arrange
        var targetDir = Path.Combine(_testDirectory, "mirror_target");
        Directory.CreateDirectory(targetDir);
        
        // Create an extra file in target that doesn't exist in backup
        var extraFile = Path.Combine(targetDir, "extra.txt");
        await File.WriteAllTextAsync(extraFile, "extra content");
        
        await CreateAndBackupFile("test.txt", 1024);
        var files = await _restoreService.ListRestorableFilesAsync();

        // Act
        var result = await _restoreService.MirrorSyncToLocalAsync(files, targetDir, _sourceDirectory);

        // Assert
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(extraFile));
    }

    [Fact]
    public async Task MirrorSyncToLocalAsync_PreservesSubdirectoryStructure()
    {
        // Arrange
        var targetDir = Path.Combine(_testDirectory, "mirror_target");
        Directory.CreateDirectory(targetDir);
        
        await CreateAndBackupFile("subdir/nested.txt", 512);
        var files = await _restoreService.ListRestorableFilesAsync();

        // Act
        await _restoreService.MirrorSyncToLocalAsync(files, targetDir, _sourceDirectory);

        // Assert
        Assert.True(File.Exists(Path.Combine(targetDir, "subdir", "nested.txt")));
    }

    #endregion

    #region RestoreFilesWithRemappingAsync Tests

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_RestoresToCustomPaths()
    {
        // Arrange
        await CreateAndBackupFile("original.txt", 1024);
        var files = await _restoreService.ListRestorableFilesAsync();
        var targetPath = Path.Combine(_restoreDirectory, "remapped.txt");
        
        var filesWithPaths = files.Select(f => (f, targetPath)).ToList();

        // Act
        var result = await _restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert
        Assert.Single(result.SuccessfulFiles);
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_CreatesTargetDirectories()
    {
        // Arrange
        await CreateAndBackupFile("test.txt", 1024);
        var files = await _restoreService.ListRestorableFilesAsync();
        var targetPath = Path.Combine(_restoreDirectory, "new", "nested", "dir", "file.txt");
        
        var filesWithPaths = files.Select(f => (f, targetPath)).ToList();

        // Act
        await _restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_HandlesMultipleFiles()
    {
        // Arrange
        await CreateAndBackupFile("file1.txt", 1024);
        await CreateAndBackupFile("file2.txt", 2048);
        var files = await _restoreService.ListRestorableFilesAsync();
        
        var filesWithPaths = files.Select(f => 
            (f, Path.Combine(_restoreDirectory, Path.GetFileName(f.LocalPath)))).ToList();

        // Act
        var result = await _restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert
        Assert.Equal(2, result.SuccessfulFiles.Count);
        Assert.Empty(result.FailedFiles);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private async Task<BackedUpFile> BackupFileAsync(string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var fileHash = await ChunkingTestHelper.ComputeFileHashForTestAsync(filePath);
        var (chunks, _) = await _chunkingService.ChunkFileForTestAsync(filePath);

        // Upload each chunk
        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk);
            chunk.BlobName = await _blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        BackedUpFile backedUpFile = new()
        {
            LocalPath = filePath,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            FileHash = fileHash,
            Chunks = chunks,
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        };
        
        await _blobService.UploadFileMetadataAsync(backedUpFile);
        _databaseService.SaveBackedUpFile(backedUpFile);
        
        return backedUpFile;
    }

    private async Task CreateAndBackupFile(string relativePath, int size)
    {
        var fullPath = Path.Combine(_sourceDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        var content = CreateRandomContent(size);
        await File.WriteAllBytesAsync(fullPath, content);
        await BackupFileAsync(fullPath);
    }

    #endregion
}
