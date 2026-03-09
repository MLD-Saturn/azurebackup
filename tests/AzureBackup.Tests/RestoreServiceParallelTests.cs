using System.Collections.Concurrent;
using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for RestoreService parallel download functionality.
/// Verifies that chunks are downloaded in parallel and assembled correctly.
/// </summary>
public class RestoreServiceParallelTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "ParallelRestoreTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ParallelRestoreTests_{Guid.NewGuid():N}");
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

    #region Parallel Download Verification Tests

    [Fact]
    public async Task RestoreFileAsync_MultipleChunks_DownloadsInParallel()
    {
        // Arrange
        DownloadTrackingBlobService trackingBlobService = new(_encryptionService, simulatedLatencyMs: 50);
        await trackingBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, trackingBlobService, _encryptionService);

        // Create a multi-chunk file
        var content = CreateRandomContent(3 * 1024 * 1024); // 3 MB - multiple chunks
        var sourceFile = Path.Combine(_sourceDirectory, "parallel_restore.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(trackingBlobService, sourceFile);
        
        // Reset tracking after backup
        trackingBlobService.ResetTracking();
        
        var restorePath = Path.Combine(_restoreDirectory, "parallel_restore.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        // Verify parallel downloads occurred
        Assert.True(trackingBlobService.MaxObservedConcurrency > 1,
            $"Expected parallel downloads, but max concurrency was {trackingBlobService.MaxObservedConcurrency}");
        
        // Verify restored content matches original
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    [Fact]
    public async Task RestoreFileAsync_ParallelDownloads_ChunksAssembledInCorrectOrder()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 10);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create a deterministic content pattern to verify ordering
        byte[] content = new byte[2 * 1024 * 1024]; // 2 MB
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 256);
        }
        
        var sourceFile = Path.Combine(_sourceDirectory, "ordered_restore.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "ordered_restore.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        
        // Verify byte-by-byte that content is in correct order
        Assert.Equal(content.Length, restoredContent.Length);
        for (int i = 0; i < content.Length; i++)
        {
            Assert.Equal(content[i], restoredContent[i]);
        }
    }

    [Fact]
    public async Task RestoreFileAsync_SingleChunk_SkipsParallelOverhead()
    {
        // Arrange
        DownloadTrackingBlobService trackingBlobService = new(_encryptionService);
        await trackingBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, trackingBlobService, _encryptionService);

        // Create a small single-chunk file
        var content = CreateRandomContent(32 * 1024); // 32 KB - single chunk
        var sourceFile = Path.Combine(_sourceDirectory, "single_chunk.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(trackingBlobService, sourceFile);
        trackingBlobService.ResetTracking();
        
        var restorePath = Path.Combine(_restoreDirectory, "single_chunk.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        // Single chunk should not trigger parallel download (max concurrency = 1)
        Assert.Equal(1, trackingBlobService.MaxObservedConcurrency);
        
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    #endregion

    #region Data Integrity Under Parallel Downloads

    [Fact]
    public async Task RestoreFileAsync_ParallelDownloads_MaintainsDataIntegrity()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 20);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Run multiple restores to verify consistency
        for (int run = 0; run < 3; run++)
        {
            var content = CreateRandomContent(2 * 1024 * 1024);
            var sourceFile = Path.Combine(_sourceDirectory, $"integrity_{run}.bin");
            await File.WriteAllBytesAsync(sourceFile, content);
            
            var backedUp = await BackupFileAsync(blobService, sourceFile);
            var restorePath = Path.Combine(_restoreDirectory, $"integrity_{run}.bin");

            // Act
            var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

            // Assert
            Assert.True(result);
            var restoredContent = await File.ReadAllBytesAsync(restorePath);
            Assert.Equal(content, restoredContent);
        }
    }

    [Fact]
    public async Task RestoreFileAsync_ConcurrentRestores_AllSucceed()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 10);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create multiple files and backup
        List<(string sourcePath, byte[] content, BackedUpFile backup)> testData = new();
        for (int i = 0; i < 5; i++)
        {
            var content = CreateRandomContent(500 * 1024); // 500 KB each
            var sourceFile = Path.Combine(_sourceDirectory, $"concurrent_{i}.bin");
            await File.WriteAllBytesAsync(sourceFile, content);
            var backedUp = await BackupFileAsync(blobService, sourceFile);
            testData.Add((sourceFile, content, backedUp));
        }

        // Act - Restore all files concurrently
        var restoreTasks = testData.Select((item, idx) =>
        {
            var restorePath = Path.Combine(_restoreDirectory, $"concurrent_{idx}.bin");
            return restoreService.RestoreFileAsync(item.backup, restorePath, true);
        }).ToList();

        var results = await Task.WhenAll(restoreTasks);

        // Assert - All should succeed
        Assert.All(results, r => Assert.True(r));

        // Verify all restored content matches
        for (int i = 0; i < testData.Count; i++)
        {
            var restorePath = Path.Combine(_restoreDirectory, $"concurrent_{i}.bin");
            var restoredContent = await File.ReadAllBytesAsync(restorePath);
            Assert.Equal(testData[i].content, restoredContent);
        }
    }

    #endregion

    #region Progress Reporting During Parallel Downloads

    [Fact]
    public async Task RestoreFileAsync_ParallelDownloads_ProgressReportedCorrectly()
    {
        // Arrange
        InMemoryBlobService blobService = new(_encryptionService, simulatedLatencyMs: 20);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(2 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "progress_test.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "progress_test.bin");

        ConcurrentBag<(long current, long total)> progressReports = new();
        Progress<(long current, long total)> progress = new(p => progressReports.Add(p));

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true, progress);

        // Assert
        Assert.True(result);
        Assert.NotEmpty(progressReports);
        
        var reports = progressReports.ToList();
        
        // Progress should be monotonically increasing (eventually)
        // Note: Due to parallelism, intermediate values may not be strictly ordered
        var finalReport = reports.MaxBy(r => r.current);
        Assert.Equal(finalReport.total, finalReport.current);
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

    #endregion
}

/// <summary>
/// Test double that tracks download concurrency.
/// </summary>
internal class DownloadTrackingBlobService : InMemoryBlobService
{
    private int _currentDownloadConcurrency;
    private readonly object _concurrencyLock = new();
    
    public int MaxObservedConcurrency { get; private set; }
    public int TotalDownloadCalls { get; private set; }

    public DownloadTrackingBlobService(EncryptionService encryptionService, int simulatedLatencyMs = 0) 
        : base(encryptionService, simulatedLatencyMs)
    {
    }

    public void ResetTracking()
    {
        lock (_concurrencyLock)
        {
            _currentDownloadConcurrency = 0;
            MaxObservedConcurrency = 0;
            TotalDownloadCalls = 0;
        }
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        lock (_concurrencyLock)
        {
            _currentDownloadConcurrency++;
            TotalDownloadCalls++;
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _currentDownloadConcurrency);
        }

        try
        {
            return await base.DownloadChunkAsync(blobName, cancellationToken);
        }
        finally
        {
            lock (_concurrencyLock)
            {
                _currentDownloadConcurrency--;
            }
        }
    }
}
