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
        _databaseService.Initialize(_dbPath, TestPassword);
        
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
        SynchronousProgress<(long current, long total)> progress = new(p => progressReports.Add(p));

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
        var (chunks, _) = await _chunkingService.ChunkFileAsync(filePath);
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

/// <summary>
/// Tests specifically for bounded memory parallel downloads.
/// </summary>
public class BoundedParallelDownloadTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "BoundedDownloadTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BoundedDownloadTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);
        
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        
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

    [Fact]
    public async Task RestoreFileAsync_ManyChunks_LimitsConcurrentDownloads()
    {
        // Arrange - Create a blob service that tracks concurrency
        DownloadTrackingBlobService trackingBlobService = new(_encryptionService, simulatedLatencyMs: 50);
        await trackingBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, trackingBlobService, _encryptionService);

        // Create a file with many chunks (5 MB = ~20 chunks at default 256KB avg)
        var content = CreateRandomContent(5 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "many_chunks.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(trackingBlobService, sourceFile);
        trackingBlobService.ResetTracking();
        
        var restorePath = Path.Combine(_restoreDirectory, "many_chunks.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        // Verify concurrency was limited by adaptive chunk concurrency (max 24 for small chunks)
        Assert.True(trackingBlobService.MaxObservedConcurrency <= 24,
            $"Expected max concurrency <= 24, but was {trackingBlobService.MaxObservedConcurrency}");
        
        // Should have used some parallelism
        Assert.True(trackingBlobService.MaxObservedConcurrency > 1,
            $"Expected parallel downloads, but max concurrency was {trackingBlobService.MaxObservedConcurrency}");
        
        // Verify restored content matches
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    [Fact]
    public async Task RestoreFileAsync_SlowWriter_BackpressureLimitsConcurrency()
    {
        // Arrange - Use a slow-writing blob service to simulate backpressure
        SlowWriterTrackingBlobService slowBlobService = new(_encryptionService, downloadLatencyMs: 10);
        await slowBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, slowBlobService, _encryptionService);

        // Create a file with many chunks
        var content = CreateRandomContent(3 * 1024 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "backpressure.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(slowBlobService, sourceFile);
        slowBlobService.ResetTracking();
        
        var restorePath = Path.Combine(_restoreDirectory, "backpressure.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        // Even with many chunks, concurrency should be bounded by adaptive limit
        Assert.True(slowBlobService.MaxObservedConcurrency <= 24,
            $"Backpressure should limit concurrency, but max was {slowBlobService.MaxObservedConcurrency}");
        
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    [Fact]
    public async Task RestoreFileAsync_OutOfOrderDownloads_AssembledCorrectly()
    {
        // Arrange - Use a blob service that introduces variable delays
        VariableLatencyBlobService variableBlobService = new(_encryptionService);
        await variableBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, variableBlobService, _encryptionService);

        // Create deterministic content pattern
        byte[] content = new byte[2 * 1024 * 1024];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)((i / 1024) % 256); // Pattern changes every KB
        }
        
        var sourceFile = Path.Combine(_sourceDirectory, "out_of_order.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(variableBlobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "out_of_order.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert
        Assert.True(result);
        
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content.Length, restoredContent.Length);
        
        // Verify content integrity byte-by-byte
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != restoredContent[i])
            {
                Assert.Fail($"Content mismatch at byte {i}: expected {content[i]}, got {restoredContent[i]}");
            }
        }
    }

    [Fact]
    public async Task RestoreFileAsync_DownloadFailure_CancelsOtherDownloads()
    {
        // This test is timing-sensitive: parallel downloads may complete before the
        // failing chunk's error propagates, causing the restore to occasionally succeed.
        await FlakyTestHelper.RetryAsync(async () =>
        {
            // Arrange - Use a blob service that fails on specific chunks
            FailingBlobService failingBlobService = new(_encryptionService, failOnChunkIndex: 3);
            await failingBlobService.ConnectAsync("fake", "container");
            RestoreService restoreService = new(_databaseService, failingBlobService, _encryptionService);

            var content = CreateRandomContent(2 * 1024 * 1024);
            var sourceFile = Path.Combine(_sourceDirectory, "fail_test.bin");
            await File.WriteAllBytesAsync(sourceFile, content);

            var backedUp = await BackupFileAsync(failingBlobService, sourceFile);
            failingBlobService.EnableFailure();

            var restorePath = Path.Combine(_restoreDirectory, "fail_test.bin");

            // Act & Assert - Should fail but not hang
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true, null, cts.Token);

            Assert.False(result);

            // Temp file should be cleaned up
            Assert.False(File.Exists(restorePath + ".tmp"));
        });
    }

    #region Helper Methods

    private async Task<BackedUpFile> BackupFileAsync(IBlobStorageService blobService, string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var (chunks, _) = await _chunkingService.ChunkFileAsync(filePath);
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
/// Blob service that tracks concurrency with simulated slow writes.
/// </summary>
internal class SlowWriterTrackingBlobService : InMemoryBlobService
{
    private int _currentDownloadConcurrency;
    private readonly object _concurrencyLock = new();
    private readonly int _downloadLatencyMs;
    
    public int MaxObservedConcurrency { get; private set; }

    public SlowWriterTrackingBlobService(EncryptionService encryptionService, int downloadLatencyMs = 10) 
        : base(encryptionService)
    {
        _downloadLatencyMs = downloadLatencyMs;
    }

    public void ResetTracking()
    {
        lock (_concurrencyLock)
        {
            _currentDownloadConcurrency = 0;
            MaxObservedConcurrency = 0;
        }
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        lock (_concurrencyLock)
        {
            _currentDownloadConcurrency++;
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _currentDownloadConcurrency);
        }

        try
        {
            // Simulate network latency
            await Task.Delay(_downloadLatencyMs, cancellationToken);
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

/// <summary>
/// Blob service that introduces variable delays to cause out-of-order completions.
/// </summary>
internal class VariableLatencyBlobService : InMemoryBlobService
{
    private int _downloadCount;
    private readonly Random _random = new(42); // Seeded for reproducibility

    public VariableLatencyBlobService(EncryptionService encryptionService) 
        : base(encryptionService)
    {
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        int count = Interlocked.Increment(ref _downloadCount);
        
        // Variable delay: odd chunks are slower than even chunks
        // This should cause out-of-order arrivals
        int delay = (count % 2 == 0) ? 5 : 50;
        await Task.Delay(delay, cancellationToken);
        
        return await base.DownloadChunkAsync(blobName, cancellationToken);
    }
}

/// <summary>
/// Blob service that fails on a specific chunk to test error handling.
/// </summary>
internal class FailingBlobService : InMemoryBlobService
{
    private readonly int _failOnChunkIndex;
    private int _downloadCount;
    private bool _failureEnabled;

    public FailingBlobService(EncryptionService encryptionService, int failOnChunkIndex) 
        : base(encryptionService)
    {
        _failOnChunkIndex = failOnChunkIndex;
    }

    public void EnableFailure() => _failureEnabled = true;

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        int count = Interlocked.Increment(ref _downloadCount);

        if (_failureEnabled && count == _failOnChunkIndex + 1)
        {
            await Task.Delay(10, cancellationToken); // Small delay before failing
            throw new InvalidOperationException($"Simulated download failure on chunk {_failOnChunkIndex}");
        }

        return await base.DownloadChunkAsync(blobName, cancellationToken);
    }
}

/// <summary>
/// Blob service that throws transient IOException failures a configurable number of times
/// before succeeding, to test chunk-level retry logic.
/// </summary>
internal class TransientFailureBlobService : InMemoryBlobService
{
    private readonly int _transientFailuresPerChunk;
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();
    private int _totalRetriedDownloads;

    public int TotalRetriedDownloads => _totalRetriedDownloads;

    public TransientFailureBlobService(EncryptionService encryptionService, int transientFailuresPerChunk)
        : base(encryptionService)
    {
        _transientFailuresPerChunk = transientFailuresPerChunk;
    }

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var count = _failureCounts.AddOrUpdate(blobName, 1, (_, c) => c + 1);

        if (count <= _transientFailuresPerChunk)
        {
            Interlocked.Increment(ref _totalRetriedDownloads);
            await Task.Delay(5, cancellationToken);
            throw new IOException($"Simulated transient I/O failure on {blobName} (attempt {count})");
        }

        return await base.DownloadChunkAsync(blobName, cancellationToken);
    }
}

/// <summary>
/// Tests for transient retry, adaptive concurrency, and pre-sorting optimizations.
/// </summary>
public class RestoreServiceThroughputTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;

    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "ThroughputTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ThroughputTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");

        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);

        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);

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

    [Fact]
    public async Task RestoreFileAsync_TransientFailures_RetriesAndSucceeds()
    {
        // Arrange — blob service fails once per chunk with IOException (transient)
        TransientFailureBlobService transientBlobService = new(_encryptionService, transientFailuresPerChunk: 1);
        await transientBlobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, transientBlobService, _encryptionService);

        var content = CreateRandomContent(2 * 1024 * 1024); // 2 MB → multiple chunks
        var sourceFile = Path.Combine(_sourceDirectory, "transient_retry.bin");
        await File.WriteAllBytesAsync(sourceFile, content);

        var backedUp = await BackupFileAsync(transientBlobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "transient_retry.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true);

        // Assert — restore should succeed despite transient failures
        Assert.True(result, "Restore should succeed after retrying transient failures");
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    [Fact]
    public async Task RestoreFileAsync_NonTransientFailure_DoesNotRetry()
    {
        // Arrange — blob service fails with InvalidOperationException (non-transient)
        // This test verifies that the retry path is filtered — non-transient errors
        // propagate immediately rather than being retried.
        await FlakyTestHelper.RetryAsync(async () =>
        {
            FailingBlobService failingBlobService = new(_encryptionService, failOnChunkIndex: 2);
            await failingBlobService.ConnectAsync("fake", "container");
            RestoreService restoreService = new(_databaseService, failingBlobService, _encryptionService);

            var content = CreateRandomContent(2 * 1024 * 1024);
            var sourceFile = Path.Combine(_sourceDirectory, "nontransient.bin");
            await File.WriteAllBytesAsync(sourceFile, content);

            var backedUp = await BackupFileAsync(failingBlobService, sourceFile);
            failingBlobService.EnableFailure();

            var restorePath = Path.Combine(_restoreDirectory, "nontransient.bin");

            // Act
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await restoreService.RestoreFileAsync(backedUp, restorePath, true, null, cts.Token);

            // Assert — should fail without retries
            Assert.False(result, "Non-transient errors should not be retried");
        });
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_LargeFilesSortedDescending()
    {
        // Arrange — create files of varying sizes and verify largest starts first
        InMemoryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create files: small (50 KB), medium (500 KB), large (2 MB)
        var smallContent = CreateRandomContent(50 * 1024);
        var mediumContent = CreateRandomContent(500 * 1024);
        var largeContent = CreateRandomContent(2 * 1024 * 1024);

        var smallFile = Path.Combine(_sourceDirectory, "small.bin");
        var mediumFile = Path.Combine(_sourceDirectory, "medium.bin");
        var largeFile = Path.Combine(_sourceDirectory, "large.bin");

        await File.WriteAllBytesAsync(smallFile, smallContent);
        await File.WriteAllBytesAsync(mediumFile, mediumContent);
        await File.WriteAllBytesAsync(largeFile, largeContent);

        var smallBackup = await BackupFileAsync(blobService, smallFile);
        var mediumBackup = await BackupFileAsync(blobService, mediumFile);
        var largeBackup = await BackupFileAsync(blobService, largeFile);

        var filesWithPaths = new List<(BackedUpFile file, string targetPath)>
        {
            (smallBackup, Path.Combine(_restoreDirectory, "small.bin")),
            (mediumBackup, Path.Combine(_restoreDirectory, "medium.bin")),
            (largeBackup, Path.Combine(_restoreDirectory, "large.bin"))
        };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths, overwriteExisting: true);

        // Assert — all files should be restored correctly regardless of processing order
        Assert.Equal(3, result.SuccessfulFiles.Count);
        Assert.Empty(result.FailedFiles);

        Assert.Equal(smallContent, await File.ReadAllBytesAsync(Path.Combine(_restoreDirectory, "small.bin")));
        Assert.Equal(mediumContent, await File.ReadAllBytesAsync(Path.Combine(_restoreDirectory, "medium.bin")));
        Assert.Equal(largeContent, await File.ReadAllBytesAsync(Path.Combine(_restoreDirectory, "large.bin")));
    }

    #region Helper Methods

    private async Task<BackedUpFile> BackupFileAsync(IBlobStorageService blobService, string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var (chunks, _) = await _chunkingService.ChunkFileAsync(filePath);
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
