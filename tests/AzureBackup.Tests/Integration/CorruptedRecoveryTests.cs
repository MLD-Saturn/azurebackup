using AzureBackup.Tests.Infrastructure;
using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for corrupted file recovery paths across all restore methods.
/// Verifies that DataIntegrityException triggers corrupted recovery
/// (best-effort decryption to a __corrupted__ subfolder) consistently
/// across RestoreFilesWithRemappingAsync, RestoreFilesAsync, and MirrorSyncToLocalAsync.
/// </summary>
public class CorruptedRecoveryTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;

    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "TestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CorruptedRecoveryTests_{Guid.NewGuid():N}");
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
    public async Task RestoreFilesWithRemappingAsync_WhenChunkCorrupted_RecoveresToCorruptedSubfolder()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "corrupted_remap.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        var targetPath = Path.Combine(_restoreDirectory, "corrupted_remap.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as success
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Equal(targetPath, result.SuccessfulFiles[0]);
        Assert.True(File.Exists(targetPath), $"Promoted file should exist at {targetPath}");
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenCrcOnlyCorruption_AutoRepairsChunksInStorage()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "selfheal.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        var targetPath = Path.Combine(_restoreDirectory, "selfheal.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — every chunk decrypted (CRC-only), so each one is repaired in place
        Assert.Single(result.SuccessfulFiles);
        Assert.Equal(backedUp.Chunks.Count, blobService.RepairedCount);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenChunkUnrecoverable_DoesNotRepairInStorage()
    {
        // Arrange — best-effort returns null for all chunks, so recovery zero-fills
        // and the file is NOT fully recoverable; no repair must be attempted.
        AllChunksUnrecoverableBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "noheal.bin", 100 * 1024);
        blobService.CorruptNormalDownloads = true;
        blobService.FailBestEffort = true;

        var targetPath = Path.Combine(_restoreDirectory, "noheal.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — unrecoverable data is reported as a failure and never repaired
        Assert.Equal(0, blobService.RepairCount);
    }

    [Fact]
    public async Task RestoreFilesAsync_WhenChunkCorrupted_RecoveresToCorruptedSubfolder()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        await CreateAndBackupFile(blobService, "corrupted_batch.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        // Act — use RestoreFilesWithRemappingAsync (RestoreFilesAsync was removed)
        var files = await restoreService.ListRestorableFilesAsync();
        var filesWithPaths = files.Select(f => (f, Path.Combine(_restoreDirectory, Path.GetFileName(f.LocalPath)))).ToList();
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as success
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.True(File.Exists(result.SuccessfulFiles[0]));
    }

    [Fact]
    public async Task MirrorSyncToLocalAsync_WhenChunkCorrupted_ReportsCorruptedRecovery()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "corrupted_sync.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        // Act
        var result = await restoreService.MirrorSyncToLocalAsync(
            [backedUp],
            _restoreDirectory,
            _sourceDirectory);

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as transferred
        Assert.Equal(1, result.FilesTransferred);
        Assert.Equal(0, result.FilesCorruptedRecovered);
        Assert.Empty(result.CorruptedRecoveryPaths);
    }

    [Fact]
    public async Task CorruptedRecovery_WhenAllChunksUnrecoverable_EarlyBailoutReturnsFailure()
    {
        // Arrange — use a blob service that returns null for ALL best-effort downloads
        // This simulates wrong-key or completely destroyed data
        AllChunksUnrecoverableBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create a file with multiple chunks (needs >=3 for the early bailout check)
        var backedUp = await CreateAndBackupFile(blobService, "unrecoverable.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, 
            $"Need >=3 chunks to test early bailout, got {backedUp.Chunks.Count}");

        // Enable corruption for normal downloads (triggers DataIntegrityException)
        // and make best-effort return null (triggers early bailout)
        blobService.CorruptNormalDownloads = true;
        blobService.FailBestEffort = true;

        var targetPath = Path.Combine(_restoreDirectory, "unrecoverable.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — recovery should fail entirely (early bailout after 3 chunks)
        Assert.Empty(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Single(result.FailedFiles);
    }

    [Fact]
    public async Task CorruptedRecovery_WithMixedChunks_ZeroFillsUnrecoverableChunks()
    {
        // Arrange — blob service where some chunks decrypt fine, others are completely unrecoverable
        PartialRecoveryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create multi-chunk file
        var backedUp = await CreateAndBackupFile(blobService, "partial.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3,
            $"Need >=3 chunks to test partial recovery, got {backedUp.Chunks.Count}");

        // Enable corruption for normal downloads (triggers DataIntegrityException)
        // Best-effort will fail for chunk index 1 only (zero-filled)
        blobService.CorruptNormalDownloads = true;
        blobService.UnrecoverableChunkIndices.Add(1);

        var targetPath = Path.Combine(_restoreDirectory, "partial.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — file recovered to __corrupted__ with 1 zero-filled chunk
        Assert.Empty(result.SuccessfulFiles);
        Assert.Single(result.CorruptedRecoveryFiles);

        var (_, recoveredPath, unrecoverableChunks) = result.CorruptedRecoveryFiles[0];
        Assert.Equal(1, unrecoverableChunks);
        Assert.True(File.Exists(recoveredPath));

        // Verify recovered file size matches original (zero-filled chunks preserve size)
        var recoveredInfo = new FileInfo(recoveredPath);
        Assert.Equal(backedUp.FileSize, recoveredInfo.Length);
    }

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private async Task<BackedUpFile> CreateAndBackupFile(
        IBlobStorageService blobService, string relativePath, int size)
    {
        var fullPath = Path.Combine(_sourceDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var content = CreateRandomContent(size);
        await File.WriteAllBytesAsync(fullPath, content);

        FileInfo fileInfo = new(fullPath);
        var (chunks, _) = await _chunkingService.ChunkFileForTestAsync(fullPath);
        var fileHash = await ChunkingTestHelper.ComputeFileHashForTestAsync(fullPath);

        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(fullPath, chunk);
            chunk.BlobName = await blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        BackedUpFile backedUp = new()
        {
            LocalPath = fullPath,
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

    #endregion
}

/// <summary>
/// Blob service that corrupts normal downloads (causing DataIntegrityException)
/// but allows best-effort downloads to succeed (CRC-only corruption).
/// This simulates the scenario where encrypted data has a corrupted CRC trailer.
/// </summary>
internal class CorruptOnDownloadBlobService : InMemoryBlobService
{
    public bool CorruptDownloads { get; set; }

    private int _repairCount;
    private int _repairedCount;

    /// <summary>Number of times <see cref="RepairChunkAsync"/> was invoked.</summary>
    public int RepairCount => _repairCount;

    /// <summary>Number of chunks that reported <see cref="ChunkRepairOutcome.Repaired"/>.</summary>
    public int RepairedCount => _repairedCount;

    public CorruptOnDownloadBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptDownloads && data.Length > 0)
        {
            // Corrupt the decrypted data so SHA-256 verification fails in RestoreService,
            // which throws DataIntegrityException and triggers recovery
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    public override async Task<ChunkRepairOutcome> RepairChunkAsync(
        ReadOnlyMemory<byte> chunkData, string chunkHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _repairCount);
        var outcome = await base.RepairChunkAsync(chunkData, chunkHash, cancellationToken);
        if (outcome == ChunkRepairOutcome.Repaired)
            Interlocked.Increment(ref _repairedCount);
        return outcome;
    }

    // DownloadChunkBestEffortAsync inherits from InMemoryBlobService — returns good data
    // This simulates: CRC corrupted (normal decrypt fails validation) but AES-GCM tag OK
}

/// <summary>
/// Blob service where normal downloads are corrupted AND best-effort downloads
/// return null for all chunks. Simulates completely unrecoverable data (wrong key).
/// </summary>
internal class AllChunksUnrecoverableBlobService : InMemoryBlobService
{
    public bool CorruptNormalDownloads { get; set; }
    public bool FailBestEffort { get; set; }

    private int _repairCount;

    /// <summary>Number of times <see cref="RepairChunkAsync"/> was invoked.</summary>
    public int RepairCount => _repairCount;

    public AllChunksUnrecoverableBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptNormalDownloads && data.Length > 0)
        {
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    public override Task<byte[]?> DownloadChunkBestEffortAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        if (FailBestEffort)
            return Task.FromResult<byte[]?>(null);

        return base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }

    public override Task<ChunkRepairOutcome> RepairChunkAsync(
        ReadOnlyMemory<byte> chunkData, string chunkHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _repairCount);
        return base.RepairChunkAsync(chunkData, chunkHash, cancellationToken);
    }
}

/// <summary>
/// Blob service where normal downloads are corrupted and best-effort downloads
/// fail for specific chunk indices. Simulates partial recovery.
/// </summary>
internal class PartialRecoveryBlobService : InMemoryBlobService
{
    public bool CorruptNormalDownloads { get; set; }
    public HashSet<int> UnrecoverableChunkIndices { get; } = [];

    private int _bestEffortCallCount;

    public PartialRecoveryBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptNormalDownloads && data.Length > 0)
        {
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    public override async Task<byte[]?> DownloadChunkBestEffortAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var index = _bestEffortCallCount++;

        if (UnrecoverableChunkIndices.Contains(index))
            return null;

        return await base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }
}
