using System.Collections.Concurrent;
using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for parallel upload/download operations to ensure thread safety
/// and correct behavior under concurrent execution.
/// </summary>
public class ParallelOperationsTests : IAsyncLifetime
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

    private const string TestPassword = "ParallelTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ParallelTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);
        
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        _restoreService = new RestoreService(_databaseService, _blobService, _encryptionService);
        
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
        
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

    #region Concurrent Upload Tests

    [Fact]
    public async Task ConcurrentChunkUploads_AllChunksUploadedCorrectly()
    {
        // Arrange - Create multiple chunks to upload concurrently
        var chunkCount = 10;
        List<(byte[] data, string hash)> chunks = new();
        
        for (int i = 0; i < chunkCount; i++)
        {
            var data = CreateRandomContent(64 * 1024); // 64 KB chunks
            var hash = ComputeHash(data);
            chunks.Add((data, hash));
        }

        // Act - Upload all chunks concurrently
        var uploadTasks = chunks.Select(chunk =>
            _blobService.UploadChunkAsync(chunk.data, chunk.hash));
        
        var blobNames = await Task.WhenAll(uploadTasks);

        // Assert - All uploads succeeded
        Assert.Equal(chunkCount, blobNames.Length);
        Assert.All(blobNames, name => Assert.StartsWith("chunks/", name));
        
        // Verify all chunks are in storage
        Assert.Equal(chunkCount, _blobService.StoredBlobNames.Count(n => n.StartsWith("chunks/")));
    }

    [Fact]
    public async Task ConcurrentChunkUploads_WithDuplicates_DeduplicatesCorrectly()
    {
        // Arrange - Create chunks with some duplicates
        var uniqueData = CreateRandomContent(64 * 1024);
        var uniqueHash = ComputeHash(uniqueData);
        
        // Try to upload the same chunk 5 times concurrently
        var uploadTasks = Enumerable.Range(0, 5)
            .Select(_ => _blobService.UploadChunkAsync(uniqueData, uniqueHash));

        // Act
        var blobNames = await Task.WhenAll(uploadTasks);

        // Assert - All return the same blob name
        Assert.All(blobNames, name => Assert.Equal($"chunks/{uniqueHash}", name));
        
        // Only one chunk should be stored (deduplication)
        Assert.Single(_blobService.StoredBlobNames, n => n.StartsWith("chunks/"));
    }

    [Fact]
    public async Task ConcurrentChunkUploads_ProgressReporting_ThreadSafe()
    {
        // Arrange
        var chunkCount = 8;
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(_ => CreateRandomContent(32 * 1024))
            .ToList();

        ConcurrentBag<long> progressValues = new();
        SynchronousProgress<long> progress = new(bytes => progressValues.Add(bytes));

        // Act - Upload concurrently with progress reporting
        var uploadTasks = chunks.Select(chunk =>
        {
            var hash = ComputeHash(chunk);
            return _blobService.UploadChunkAsync(chunk, hash, progress: progress);
        });

        await Task.WhenAll(uploadTasks);

        // Assert - Progress was reported for each chunk
        Assert.Equal(chunkCount, progressValues.Count);
    }

    #endregion

    #region Concurrent Download Tests

    [Fact]
    public async Task ConcurrentChunkDownloads_AllChunksDownloadedCorrectly()
    {
        // Arrange - Upload multiple chunks first
        var chunkCount = 10;
        Dictionary<string, byte[]> originalChunks = new();
        
        for (int i = 0; i < chunkCount; i++)
        {
            var data = CreateRandomContent(64 * 1024);
            var hash = ComputeHash(data);
            var blobName = await _blobService.UploadChunkAsync(data, hash);
            originalChunks[blobName] = data;
        }

        // Act - Download all chunks concurrently
        var downloadTasks = originalChunks.Keys.Select(blobName =>
            _blobService.DownloadChunkAsync(blobName));
        
        var downloadedChunks = await Task.WhenAll(downloadTasks);

        // Assert - All downloads succeeded and match originals
        Assert.Equal(chunkCount, downloadedChunks.Length);
        
        var originalValues = originalChunks.Values.ToList();
        foreach (var downloaded in downloadedChunks)
        {
            Assert.Contains(originalValues, original => original.SequenceEqual(downloaded));
        }
    }

    [Fact]
    public async Task ConcurrentDownloads_SameChunk_ReturnsConsistentData()
    {
        // Arrange - Upload a single chunk
        var originalData = CreateRandomContent(128 * 1024);
        var hash = ComputeHash(originalData);
        var blobName = await _blobService.UploadChunkAsync(originalData, hash);

        // Act - Download the same chunk 10 times concurrently
        var downloadTasks = Enumerable.Range(0, 10)
            .Select(_ => _blobService.DownloadChunkAsync(blobName));
        
        var results = await Task.WhenAll(downloadTasks);

        // Assert - All downloads return identical data
        Assert.All(results, downloaded => Assert.Equal(originalData, downloaded));
    }

    #endregion

    #region Mixed Concurrent Operations Tests

    [Fact]
    public async Task MixedConcurrentOperations_UploadsAndDownloads_NoDataCorruption()
    {
        // Arrange - Pre-upload some chunks
        ConcurrentDictionary<string, byte[]> existingChunks = new();
        for (int i = 0; i < 5; i++)
        {
            var data = CreateRandomContent(32 * 1024);
            var hash = ComputeHash(data);
            var blobName = await _blobService.UploadChunkAsync(data, hash);
            existingChunks[blobName] = data;
        }

        // Create new chunks to upload
        var newChunks = Enumerable.Range(0, 5)
            .Select(_ => CreateRandomContent(32 * 1024))
            .ToList();

        // Act - Mix uploads and downloads concurrently
        List<Task> tasks = new();
        
        // Download existing chunks
        foreach (var blobName in existingChunks.Keys)
        {
            tasks.Add(Task.Run(async () =>
            {
                var downloaded = await _blobService.DownloadChunkAsync(blobName);
                Assert.Equal(existingChunks[blobName], downloaded);
            }));
        }
        
        // Upload new chunks
        foreach (var chunk in newChunks)
        {
            tasks.Add(Task.Run(async () =>
            {
                var hash = ComputeHash(chunk);
                await _blobService.UploadChunkAsync(chunk, hash);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - All operations completed without corruption
        Assert.Equal(10, _blobService.StoredBlobNames.Count(n => n.StartsWith("chunks/")));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ConcurrentUploads_WithCancellation_HandlesGracefully()
    {
        // Arrange
        CancellationTokenSource cts = new();
        var chunks = Enumerable.Range(0, 20)
            .Select(_ => CreateRandomContent(16 * 1024))
            .ToList();

        // Act - Start uploads and cancel after a short delay
        var uploadTasks = chunks.Select(async chunk =>
        {
            var hash = ComputeHash(chunk);
            try
            {
                await _blobService.UploadChunkAsync(chunk, hash, cancellationToken: cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }).ToList();

        // Cancel after starting
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        var results = await Task.WhenAll(uploadTasks);

        // Assert - Some may have completed, some may have been cancelled
        // The important thing is no exceptions escaped
        Assert.True(results.Length == chunks.Count);
    }

    #endregion

    #region Semaphore Limiting Tests

    [Fact]
    public async Task ParallelUploads_RespectsSemaphoreLimit()
    {
        // Arrange
        var maxConcurrency = 4;
        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        object concurrencyLock = new();
        
        var chunks = Enumerable.Range(0, 20)
            .Select(_ => CreateRandomContent(8 * 1024))
            .ToList();

        using SemaphoreSlim semaphore = new(maxConcurrency);

        // Act
        var tasks = chunks.Select(async chunk =>
        {
            await semaphore.WaitAsync();
            try
            {
                lock (concurrencyLock)
                {
                    currentConcurrency++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, currentConcurrency);
                }

                var hash = ComputeHash(chunk);
                await _blobService.UploadChunkAsync(chunk, hash);
                
                // Simulate some work
                await Task.Delay(10);
            }
            finally
            {
                lock (concurrencyLock)
                {
                    currentConcurrency--;
                }
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // Assert - Max concurrency should not exceed the limit
        Assert.True(maxObservedConcurrency <= maxConcurrency,
            $"Max observed concurrency ({maxObservedConcurrency}) exceeded limit ({maxConcurrency})");
    }

    #endregion

    #region Thread Safety for Progress Tracking

    [Fact]
    public async Task ParallelUploads_ProgressAccumulation_ThreadSafe()
    {
        // Arrange
        var chunks = Enumerable.Range(0, 10)
            .Select(_ => CreateRandomContent(32 * 1024))
            .ToList();
        
        long totalBytesReported = 0;
        object reportLock = new();

        // Act
        var tasks = chunks.Select(async chunk =>
        {
            var hash = ComputeHash(chunk);
            SynchronousProgress<long> progress = new(bytes =>
            {
                lock (reportLock)
                {
                    totalBytesReported += bytes;
                }
            });
            await _blobService.UploadChunkAsync(chunk, hash, progress: progress);
        }).ToList();

        await Task.WhenAll(tasks);

        // Assert - Total bytes reported should be positive
        Assert.True(totalBytesReported > 0);
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

    #endregion
}
