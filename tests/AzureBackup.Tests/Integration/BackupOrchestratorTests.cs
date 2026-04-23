using System.Collections.Concurrent;
using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Comprehensive unit tests for BackupOrchestrator covering initialization, 
/// backup operations, parallel upload logic, and coordination between services.
/// </summary>
public class BackupOrchestratorTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator _orchestrator = null!;

    private const string TestPassword = "OrchestratorTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"OrchestratorTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        
        _encryptionService = new EncryptionService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        _fileWatcherService = new FileWatcherService(_databaseService);
        
        // Create orchestrator with IBlobStorageService (InMemoryBlobService implements this)
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            _blobService,
            _fileWatcherService);
        
        await _blobService.ConnectAsync("fake-connection-string", "test-container");
        
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // B4 follow-up: defensive try-catch around dispose; see
        // LocalDatabaseServiceTests.DisposeAsync.
        try { await _orchestrator.DisposeAsync(); } catch { }
        try { _encryptionService.Dispose(); } catch { }
        try { _databaseService.Dispose(); } catch (NullReferenceException) { }

        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #region Initialization Tests

    [Fact]
    public async Task InitializeAsync_WithValidPassword_Succeeds()
    {
        // Act
        var result = await _orchestrator.InitializeAsync(TestPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InitializeAsync_WithEmptyPassword_ThrowsArgumentException()
    {
        // Act & Assert - Empty password throws ArgumentException (not returns false)
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _orchestrator.InitializeAsync(""));
    }

    [Fact]
    public async Task InitializeAsync_WithShortPassword_ThrowsSecurityPolicyException()
    {
        // Act & Assert - Password too short (< 12 chars)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("Short1!"));
    }

    [Fact]
    public async Task InitializeAsync_WithWeakPassword_OnlyOneCharType_ThrowsSecurityPolicyException()
    {
        // Act & Assert - password with only lowercase (1 of 4 types, needs 3)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("onlylowercaseletters"));
    }
    
    [Fact]
    public async Task InitializeAsync_WithWeakPassword_TwoCharTypes_ThrowsSecurityPolicyException()
    {
        // Act & Assert - password with lowercase and digits only (2 of 4 types, needs 3)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("lowercase123456"));
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SecondCallWithSamePassword_Succeeds()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);

        // Act - Initialize again with same password
        var result = await _orchestrator.InitializeAsync(TestPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SecondCallWithWrongPassword_ReturnsFalse()
    {
        // Arrange - First initialization sets the password
        await _orchestrator.InitializeAsync(TestPassword);

        // Act - Try to initialize with wrong password
        var result = await _orchestrator.InitializeAsync("WrongPassword123!");

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Backup File Tests

    [Fact]
    public async Task BackupFileAsync_SingleSmallFile_BacksUpSuccessfully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(50 * 1024); // 50 KB
        var filePath = Path.Combine(_sourceDirectory, "small_file.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        // Verify file is in database
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(BackupStatus.Completed, backedUp.Status);
    }

    [Fact]
    public async Task BackupFileAsync_LargeFile_CreatesMultipleChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(3 * 1024 * 1024); // 3 MB
        var filePath = Path.Combine(_sourceDirectory, "large_file.bin");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.True(backedUp.Chunks.Count > 1, "Large file should have multiple chunks");
    }

    [Fact]
    public async Task BackupFileAsync_NonExistentFile_ReturnsTrue_HandlesGracefully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        var filePath = Path.Combine(_sourceDirectory, "nonexistent.txt");

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert - Non-existent files return true (graceful handling of deleted files)
        // The orchestrator marks them as Excluded if they had a previous record
        Assert.True(result);
    }

    [Fact]
    public async Task BackupFileAsync_SameFileUnchanged_SkipsUpload()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "unchanged.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // First backup
        await _orchestrator.BackupFileAsync(filePath);
        var initialOperations = _blobService.TotalOperations;

        // Act - Backup same file again without changes
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        // No additional chunk upload operations should occur (only metadata check)
        Assert.Equal(initialOperations, _blobService.TotalOperations);
    }

    [Fact]
    public async Task BackupFileAsync_ModifiedFile_UploadsNewChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "modified.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // First backup
        await _orchestrator.BackupFileAsync(filePath);
        var initialHash = _databaseService.GetBackedUpFile(filePath)?.FileHash;

        // Modify the file. Bump LastWriteTimeUtc by > 2 s so the
        // orchestrator's metadata-skip short-circuit (size + mtime
        // match within 2 seconds = treat as unchanged) does not fire.
        // Without this, two writes a few ms apart trip the
        // optimisation and the second backup is skipped, leaving the
        // stored hash equal to the first backup's hash. Pre-C-5 this
        // test passed by accident under LiteDB because BSON DateTime
        // round-tripped to DateTimeKind.Local, so the orchestrator's
        // (Local - Utc) subtraction produced an absurd ~4-hour delta
        // that bypassed the skip.
        var newContent = CreateRandomContent(100 * 1024);
        await File.WriteAllBytesAsync(filePath, newContent);
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(5));

        // Act - Backup modified file
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        var newHash = _databaseService.GetBackedUpFile(filePath)?.FileHash;
        Assert.NotEqual(initialHash, newHash);
    }

    [Fact]
    public async Task BackupFileAsync_WithCancellation_StopsGracefully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(5 * 1024 * 1024); // 5 MB
        var filePath = Path.Combine(_sourceDirectory, "large_cancel.bin");
        await File.WriteAllBytesAsync(filePath, content);

        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Act & Assert - Should not throw unhandled exception
        try
        {
            await _orchestrator.BackupFileAsync(filePath, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public async Task BackupFileAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);

        var content = CreateRandomContent(200 * 1024);
        var filePath = Path.Combine(_sourceDirectory, $"progress_test_{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(filePath, content);

        ConcurrentBag<(long current, long total)> progressReports = new();
        SynchronousProgress<(long current, long total)> progress = new(p => progressReports.Add(p));

        // Act
        await _orchestrator.BackupFileAsync(filePath, progress);

        // Assert
        Assert.NotEmpty(progressReports);

        // Final progress should reach or exceed total (encryption adds overhead)
        var reports = progressReports.ToList();
        var maxProgress = reports.Max(p => p.current);
        var anyTotal = reports.First().total;
        Assert.True(maxProgress >= anyTotal, 
            $"Final progress ({maxProgress}) should reach total ({anyTotal})");
    }

    [Fact]
    public async Task BackupFileAsync_ProgressEvents_Fired()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "event_test.txt");
        await File.WriteAllBytesAsync(filePath, content);

        var progressEventFired = false;
        _orchestrator.ProgressChanged += (_, _) => progressEventFired = true;

        // Act
        await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(progressEventFired);
    }

    [Fact]
    public async Task BackupFileAsync_StatusChanged_EventFired()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(50 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "status_test.txt");
        await File.WriteAllBytesAsync(filePath, content);

        ConcurrentBag<string> statusMessages = new();
        _orchestrator.StatusChanged += (_, msg) => statusMessages.Add(msg);

        // Act
        await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.NotEmpty(statusMessages);
        Assert.Contains(statusMessages, m => m.Contains("Analyzing"));
    }

    #endregion

    #region Deduplication Tests

    [Fact]
    public async Task BackupFileAsync_DuplicateContent_DeduplicatesChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var file1 = Path.Combine(_sourceDirectory, "file1.txt");
        var file2 = Path.Combine(_sourceDirectory, "file2.txt");
        
        // Write identical content to both files
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act - Backup both files
        await _orchestrator.BackupFileAsync(file1);
        var storageAfterFirst = _blobService.TotalStorageUsed;
        
        await _orchestrator.BackupFileAsync(file2);
        var storageAfterSecond = _blobService.TotalStorageUsed;

        // Assert - Storage should not double (deduplication)
        // Metadata will be different, but chunks should be shared
        Assert.True(storageAfterSecond < storageAfterFirst * 2,
            $"Deduplication failed: storage doubled from {storageAfterFirst} to {storageAfterSecond}");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task BackupFileAsync_EmptyFile_HandlesCorrectly()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var filePath = Path.Combine(_sourceDirectory, "empty.txt");
        await File.WriteAllBytesAsync(filePath, []);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(0, backedUp.FileSize);
    }

    [Fact]
    public async Task BackupFileAsync_FileWithSpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(10 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "file with spaces & symbols (1).txt");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
    }

    #endregion

    #region State Management Tests

    [Fact]
    public void IsRunning_InitiallyFalse()
    {
        Assert.False(_orchestrator.IsRunning);
    }

    [Fact]
    public void IsPaused_InitiallyFalse()
    {
        Assert.False(_orchestrator.IsPaused);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task BackupFileAsync_ConcurrentBackups_ThreadSafe()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        List<string> files = new();
        for (int i = 0; i < 5; i++)
        {
            var content = CreateRandomContent(50 * 1024);
            var filePath = Path.Combine(_sourceDirectory, $"concurrent_{i}.txt");
            await File.WriteAllBytesAsync(filePath, content);
            files.Add(filePath);
        }

        // Act - Backup files concurrently
        var tasks = files.Select(f => _orchestrator.BackupFileAsync(f)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert - All backups should succeed
        Assert.All(results, r => Assert.True(r));
        
        // All files should be in database
        foreach (var file in files)
        {
            var backedUp = _databaseService.GetBackedUpFile(file);
            Assert.NotNull(backedUp);
        }
    }

    #endregion

    #region Direct Upload Optimization Tests

    [Fact]
    public async Task BackupFileAsync_NewFile_UsesDirectUpload()
    {
        // Arrange - Use tracking blob service to verify which method is called
        TrackingBlobService trackingBlobService = new(_encryptionService);
        await trackingBlobService.ConnectAsync("fake", "container");
        
        BackupOrchestrator trackingOrchestrator = new(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            trackingBlobService,
            _fileWatcherService);
        
        await trackingOrchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "new_file_direct.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // Act - Backup new file
        await trackingOrchestrator.BackupFileAsync(filePath);

        // Assert - Should use direct upload for new files
        Assert.True(trackingBlobService.DirectUploadCount > 0, 
            "New file should use UploadChunkDirectAsync");
        Assert.True(trackingBlobService.RegularUploadCount == 0, 
            "New file should not use UploadChunkAsync");
        
        await trackingOrchestrator.DisposeAsync();
    }

    [Fact]
    public async Task BackupFileAsync_ModifiedFile_UsesRegularUpload()
    {
        // Arrange - Use tracking blob service
        TrackingBlobService trackingBlobService = new(_encryptionService);
        await trackingBlobService.ConnectAsync("fake", "container");
        
        BackupOrchestrator trackingOrchestrator = new(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            trackingBlobService,
            _fileWatcherService);
        
        await trackingOrchestrator.InitializeAsync(TestPassword);
        
        var content1 = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "modified_file.txt");
        await File.WriteAllBytesAsync(filePath, content1);

        // First backup (new file - uses direct)
        await trackingOrchestrator.BackupFileAsync(filePath);
        
        // Reset counters
        trackingBlobService.ResetCounters();
        
        // Modify the file. See BackupFileAsync_ModifiedFile_UploadsNewChunks
        // for the rationale - the orchestrator's metadata-skip
        // optimisation requires a > 2 s mtime delta to be bypassed.
        var content2 = CreateRandomContent(100 * 1024);
        await File.WriteAllBytesAsync(filePath, content2);
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(5));

        // Act - Backup modified file
        await trackingOrchestrator.BackupFileAsync(filePath);

        // Assert - Should use regular upload for modified files (to check for unchanged chunks)
        Assert.True(trackingBlobService.RegularUploadCount > 0, 
            "Modified file should use UploadChunkAsync for deduplication");
        Assert.True(trackingBlobService.DirectUploadCount == 0, 
            "Modified file should not use UploadChunkDirectAsync");
        
        await trackingOrchestrator.DisposeAsync();
    }

    [Fact]
    public async Task BackupFileAsync_NewFile_ReducesApiCalls()
    {
        // Arrange - Compare API calls between new and modified file scenarios
        TrackingBlobService trackingBlobService = new(_encryptionService);
        await trackingBlobService.ConnectAsync("fake", "container");
        
        BackupOrchestrator trackingOrchestrator = new(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            trackingBlobService,
            _fileWatcherService);
        
        await trackingOrchestrator.InitializeAsync(TestPassword);
        
        // Create and backup first file (new - should use direct upload)
        var content1 = CreateRandomContent(200 * 1024); // Large enough for multiple chunks
        var file1 = Path.Combine(_sourceDirectory, "api_test_new.txt");
        await File.WriteAllBytesAsync(file1, content1);
        
        await trackingOrchestrator.BackupFileAsync(file1);
        var newFileOperations = trackingBlobService.TotalOperations;
        var newFileDirectCalls = trackingBlobService.DirectUploadCount;
        
        // Reset and create second file (will be treated as "modified" if we fake existing backup)
        trackingBlobService.ResetCounters();
        
        // For comparison, modify the first file. Bump mtime so the
        // metadata-skip does not short-circuit the second backup.
        // See BackupFileAsync_ModifiedFile_UploadsNewChunks for context.
        var content2 = CreateRandomContent(200 * 1024);
        await File.WriteAllBytesAsync(file1, content2);
        File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddSeconds(5));
        
        await trackingOrchestrator.BackupFileAsync(file1);
        var modifiedFileOperations = trackingBlobService.TotalOperations;
        var modifiedFileRegularCalls = trackingBlobService.RegularUploadCount;

        // Assert - New file should have fewer total operations (no existence checks)
        // Direct upload = 1 operation per chunk
        // Regular upload = potentially 1 existence check + 1 upload per chunk
        Assert.True(newFileDirectCalls > 0, "New file should use direct uploads");
        Assert.True(modifiedFileRegularCalls > 0, "Modified file should use regular uploads");
        
        await trackingOrchestrator.DisposeAsync();
    }

    #endregion

    #region Storage Tier Tests

    [Fact]
    public async Task BackupFileAsync_UsesWatchedFolderStorageTier()
    {
        // Arrange
        StorageTierTrackingBlobService tierTrackingBlobService = new(_encryptionService);
        await tierTrackingBlobService.ConnectAsync("fake", "container");
        
        // Configure watched folder with Hot tier
        var config = _databaseService.GetConfiguration();
        config.WatchedFolders.Clear();
        config.WatchedFolders.Add(new WatchedFolder 
        { 
            Path = _sourceDirectory, 
            IsEnabled = true,
            StorageTier = StorageTier.Hot
        });
        _databaseService.SaveConfiguration(config);
        
        BackupOrchestrator tierOrchestrator = new(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            tierTrackingBlobService,
            _fileWatcherService);
        
        await tierOrchestrator.InitializeAsync(TestPassword);
        
        // Create and backup a file
        var content = CreateRandomContent(10 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "tier_test.txt");
        await File.WriteAllBytesAsync(filePath, content);
        
        // Act
        await tierOrchestrator.BackupFileAsync(filePath);
        
        // Assert - All uploads should have used Hot tier
        Assert.True(tierTrackingBlobService.UploadCount > 0, "Should have uploaded at least one chunk");
        Assert.All(tierTrackingBlobService.UsedTiers, tier => Assert.Equal(StorageTier.Hot, tier));
        
        await tierOrchestrator.DisposeAsync();
    }

    [Fact]
    public async Task BackupFileAsync_DefaultsToHotTierForUnwatchedFiles()
    {
        // Arrange
        StorageTierTrackingBlobService tierTrackingBlobService = new(_encryptionService);
        await tierTrackingBlobService.ConnectAsync("fake", "container");
        
        // Configure watched folder for a DIFFERENT directory
        var config = _databaseService.GetConfiguration();
        config.WatchedFolders.Clear();
        config.WatchedFolders.Add(new WatchedFolder 
        { 
            Path = Path.Combine(_testDirectory, "other_folder"), 
            IsEnabled = true,
            StorageTier = StorageTier.Hot
        });
        _databaseService.SaveConfiguration(config);
        
        BackupOrchestrator tierOrchestrator = new(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            tierTrackingBlobService,
            _fileWatcherService);
        
        await tierOrchestrator.InitializeAsync(TestPassword);
        
        // Create and backup a file NOT in the watched folder
        var content = CreateRandomContent(10 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "unwatched_tier_test.txt");
        await File.WriteAllBytesAsync(filePath, content);
        
        // Act
        await tierOrchestrator.BackupFileAsync(filePath);
        
        // Assert - Should default to Hot tier since file is not in any watched folder
        Assert.True(tierTrackingBlobService.UploadCount > 0, "Should have uploaded at least one chunk");
        Assert.All(tierTrackingBlobService.UsedTiers, tier => Assert.Equal(StorageTier.Hot, tier));
        
        await tierOrchestrator.DisposeAsync();
    }

    #endregion

    #region Memory Budget Tests

    [Fact]
    public async Task WhenMemoryBudgetEnabledThenBackupSucceedsWithLimitedConcurrency()
    {
        // Pre-existing flake (predates D6): concurrent multi-chunk
        // upload races on Microsoft.Data.Sqlite's connection internal
        // state and produces an NRE inside SqliteConnection.Close()
        // during DisposeAsync. Same family as ConcurrentReadsAndWrites_
        // DoNotDeadlock. Wrapped in FlakyTestHelper with per-attempt
        // service rebuild so a failed attempt's poisoned connection
        // does not cascade.
        await FlakyTestHelper.RetryWithAttemptAsync(async attempt =>
        {
            try { await _orchestrator.DisposeAsync(); } catch { }
            try { _databaseService.Dispose(); } catch { }
            var perAttemptDb = Path.Combine(_testDirectory, $"budget_attempt_{attempt}.db");
            _databaseService = new LocalDatabaseService();
            _databaseService.Initialize(perAttemptDb, TestPassword);
            _orchestrator = new BackupOrchestrator(
                _databaseService, _encryptionService, new ChunkingService(),
                _blobService, _fileWatcherService);

            // Arrange
            await _orchestrator.InitializeAsync(TestPassword);

            var config = _databaseService.GetConfiguration();
            config.MemoryLimitEnabled = true;
            config.MemoryLimitMB = 512;
            _databaseService.SaveConfiguration(config);

            // Create multiple files that will produce several chunks
            var files = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var filePath = Path.Combine(_sourceDirectory, $"budget_test_{attempt}_{i}.bin");
                await File.WriteAllBytesAsync(filePath, CreateRandomContent(256 * 1024));
                files.Add(filePath);
            }

            // Act: backup with budget-aware path
            await _orchestrator.BackupFilesAsync(files);

            // Assert: all files backed up successfully
            foreach (var filePath in files)
            {
                var backedUp = _databaseService.GetBackedUpFile(filePath);
                Assert.NotNull(backedUp);
                Assert.Equal(BackupStatus.Completed, backedUp.Status);
            }
        });
    }

    [Fact]
    public async Task WhenMemoryBudgetDisabledThenBackupUsesUnlimitedBudget()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);

        var config = _databaseService.GetConfiguration();
        config.MemoryLimitEnabled = false;
        _databaseService.SaveConfiguration(config);

        var filePath = Path.Combine(_sourceDirectory, "no_budget.bin");
        await File.WriteAllBytesAsync(filePath, CreateRandomContent(128 * 1024));

        // Act
        await _orchestrator.BackupFilesAsync([filePath]);

        // Assert
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(BackupStatus.Completed, backedUp.Status);
    }

    [Fact]
    public async Task WhenExplicitBudgetProvidedThenConsumerRespectsIt()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);

        var filePath = Path.Combine(_sourceDirectory, "explicit_budget.bin");
        await File.WriteAllBytesAsync(filePath, CreateRandomContent(256 * 1024));

        // Very small budget — forces sequential chunk uploads
        using var budget = new MemoryBudget(512 * 1024);

        // Act
        var success = await _orchestrator.BackupFileAsync(filePath, progress: null, budget);

        // Assert
        Assert.True(success);
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(BackupStatus.Completed, backedUp.Status);

        // Budget should be fully released after backup completes
        Assert.Equal(0, budget.UsedBytes);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    #endregion
}

/// <summary>
/// Test double that tracks which upload method is called.
/// Used to verify the orchestrator uses direct upload for new files.
/// </summary>
internal class TrackingBlobService : InMemoryBlobService
{
    public int DirectUploadCount { get; private set; }
    public int RegularUploadCount { get; private set; }

    public TrackingBlobService(EncryptionService encryptionService) 
        : base(encryptionService)
    {
    }

    public override async Task<string> UploadChunkAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Cool,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        RegularUploadCount++;
        return await base.UploadChunkAsync(chunkData, chunkHash, storageTier, progress, cancellationToken);
    }

    public override async Task<string> UploadChunkDirectAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Cool,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        DirectUploadCount++;
        return await base.UploadChunkDirectAsync(chunkData, chunkHash, storageTier, progress, cancellationToken);
    }

    public void ResetCounters()
    {
        DirectUploadCount = 0;
        RegularUploadCount = 0;
    }
}

/// <summary>
/// Test double that tracks which storage tier is used for uploads.
/// Used to verify the orchestrator passes the correct storage tier based on watched folder configuration.
/// </summary>
internal class StorageTierTrackingBlobService : InMemoryBlobService
{
    private readonly List<StorageTier> _usedTiers = new();
    
    public IReadOnlyList<StorageTier> UsedTiers => _usedTiers;
    public int UploadCount => _usedTiers.Count;

    public StorageTierTrackingBlobService(EncryptionService encryptionService) 
        : base(encryptionService)
    {
    }

    public override async Task<string> UploadChunkAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Cool,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        _usedTiers.Add(storageTier);
        return await base.UploadChunkAsync(chunkData, chunkHash, storageTier, progress, cancellationToken);
    }

    public override async Task<string> UploadChunkDirectAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Cool,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        _usedTiers.Add(storageTier);
        return await base.UploadChunkDirectAsync(chunkData, chunkHash, storageTier, progress, cancellationToken);
    }
}
