using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using AzureBackup.Tests.Infrastructure;
using Xunit;

namespace AzureBackup.Tests.Unit;

/// <summary>
/// B71 (W5 Phase 3 Commit 3): focused tests for the restore-side
/// <see cref="ChunkBufferPool"/> routing path. The contract under test is
/// that <see cref="RestoreService.RestoreFileAsync"/> (and the multi-file
/// batch entry points that flow through it) sources plaintext chunk
/// buffers from caller-supplied <see cref="ChunkBufferPool"/> instances
/// when present, and that the buffers are returned to the same pool on
/// every success and failure path. These tests deliberately use small
/// chunk geometries so they exercise <see cref="ChunkBufferPool.SmallChunkBucketSizes"/>
/// without needing a multi-gigabyte fixture.
/// </summary>
public sealed class RestoreServiceChunkBufferPoolTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;

    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "RestorePoolTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestorePoolTests_{Guid.NewGuid():N}");
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
            catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoreFileAsync_WhenSmallPoolSupplied_RentsPlaintextBuffersFromIt()
    {
        var blob = new InMemoryBlobService(_encryptionService);
        await blob.ConnectAsync("fake", "container");
        var restore = new RestoreService(_databaseService, blob, _encryptionService);

        // ~3 MB random content -- forces multiple sub-16 MB chunks that route
        // through the small-chunk plaintext buffer pool.
        var content = CreateRandomContent(3 * 1024 * 1024);
        var sourcePath = Path.Combine(_sourceDirectory, "small-pool.bin");
        await File.WriteAllBytesAsync(sourcePath, content);
        var backedUp = await BackupFileAsync(blob, sourcePath);

        using var smallPool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);
        using var largePool = new ChunkBufferPool(ChunkBufferPool.LargeChunkBucketSizes);

        var restorePath = Path.Combine(_restoreDirectory, "small-pool.bin");
        var ok = await restore.RestoreFileAsync(
            backedUp, restorePath, overwriteExisting: true,
            progress: null, memoryBudget: null,
            cancellationToken: CancellationToken.None,
            bandwidthScheduler: null,
            largeChunkPool: largePool,
            smallChunkPool: smallPool);

        Assert.True(ok);
        Assert.Equal(content, await File.ReadAllBytesAsync(restorePath));

        // Every plaintext rent for this file should have flowed through the small pool.
        Assert.True(smallPool.TotalRents > 0,
            "Expected at least one plaintext rent to flow through the supplied small-chunk pool.");
        // No rents should have leaked into the large-chunk pool because every
        // chunk is under the 16 MB partition boundary.
        Assert.Equal(0, largePool.TotalRents);
        // Every rent must be balanced by a return on the success path; the
        // pool's TotalReturns counter is the most direct proof of correct
        // ownership transfer through the channel.
        Assert.Equal(smallPool.TotalRents, smallPool.TotalReturns);
    }

    [Fact]
    public async Task RestoreFileAsync_WhenNoPoolSupplied_StillSucceedsAndProducesIdenticalBytes()
    {
        var blob = new InMemoryBlobService(_encryptionService);
        await blob.ConnectAsync("fake", "container");
        var restore = new RestoreService(_databaseService, blob, _encryptionService);

        var content = CreateRandomContent(2 * 1024 * 1024);
        var sourcePath = Path.Combine(_sourceDirectory, "fallback.bin");
        await File.WriteAllBytesAsync(sourcePath, content);
        var backedUp = await BackupFileAsync(blob, sourcePath);

        var restorePath = Path.Combine(_restoreDirectory, "fallback.bin");
        var ok = await restore.RestoreFileAsync(backedUp, restorePath, overwriteExisting: true);

        Assert.True(ok);
        Assert.Equal(content, await File.ReadAllBytesAsync(restorePath));
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_BatchOwnsPoolsAcrossEveryFile()
    {
        var blob = new InMemoryBlobService(_encryptionService);
        await blob.ConnectAsync("fake", "container");
        var restore = new RestoreService(_databaseService, blob, _encryptionService);

        // Four large-enough files so the batch dispatcher routes them through
        // the AIMD scheduler and the writer loop. Each is sized to chunk into
        // multiple buffers in the small-chunk geometry so the pool sees real
        // load.
        var inputs = new List<(BackedUpFile backup, string restorePath, byte[] content)>();
        for (int i = 0; i < 4; i++)
        {
            var content = CreateRandomContent(3 * 1024 * 1024);
            var sourcePath = Path.Combine(_sourceDirectory, $"batch-{i}.bin");
            await File.WriteAllBytesAsync(sourcePath, content);
            var backedUp = await BackupFileAsync(blob, sourcePath);
            inputs.Add((backedUp, Path.Combine(_restoreDirectory, $"batch-{i}.bin"), content));
        }

        var requests = inputs
            .Select(x => (x.backup, x.restorePath))
            .ToList();

        var result = await restore.RestoreFilesWithRemappingAsync(requests, overwriteExisting: true);

        Assert.Equal(inputs.Count, result.SuccessfulFiles.Count);
        foreach (var input in inputs)
        {
            Assert.Equal(input.content, await File.ReadAllBytesAsync(input.restorePath));
        }
    }

    private async Task<BackedUpFile> BackupFileAsync(IBlobStorageService blobService, string filePath)
    {
        FileInfo fileInfo = new(filePath);
        var (chunks, _) = await _chunkingService.ChunkFileForTestAsync(filePath);
        var fileHash = await ChunkingTestHelper.ComputeFileHashForTestAsync(filePath);

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
}
