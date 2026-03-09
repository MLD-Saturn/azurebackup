using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for LocalDatabaseService covering CRUD operations, transactions,
/// statistics, and thread safety.
/// </summary>
public class LocalDatabaseServiceTests : IAsyncLifetime
{
    private LocalDatabaseService _databaseService = null!;
    private string _testDirectory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath);
        
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _databaseService.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    #region Initialization Tests

    [Fact]
    public void Initialize_CreatesDatabase()
    {
        // Assert
        Assert.True(_databaseService.IsInitialized);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void Initialize_WithNullPath_ThrowsArgumentNullException()
    {
        // Arrange
        using LocalDatabaseService service = new();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => service.Initialize(null!));
    }

    [Fact]
    public void Initialize_CreatesDirectoryIfNeeded()
    {
        // Arrange
        using LocalDatabaseService service = new();
        var nestedPath = Path.Combine(_testDirectory, "nested", "deep", "db.db");

        // Act
        service.Initialize(nestedPath);

        // Assert
        Assert.True(File.Exists(nestedPath));
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void GetConfiguration_ReturnsDefaultConfiguration()
    {
        // Act
        var config = _databaseService.GetConfiguration();

        // Assert
        Assert.NotNull(config);
        Assert.Equal(1, config.Id);
    }

    [Fact]
    public void SaveConfiguration_PersistsData()
    {
        // Arrange
        var config = _databaseService.GetConfiguration();
        config.ContainerName = "test-container";
        config.PasswordSalt = new byte[] { 1, 2, 3, 4 };

        // Act
        _databaseService.SaveConfiguration(config);
        var retrieved = _databaseService.GetConfiguration();

        // Assert
        Assert.Equal("test-container", retrieved.ContainerName);
        Assert.Equal(config.PasswordSalt, retrieved.PasswordSalt);
    }

    [Fact]
    public void SaveConfiguration_WithNull_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _databaseService.SaveConfiguration(null!));
    }

    [Fact]
    public void IsConfigured_WithNoConfig_ReturnsFalse()
    {
        // Act
        var isConfigured = _databaseService.IsConfigured();

        // Assert
        Assert.False(isConfigured);
    }


    [Fact]
    public void IsConfigured_WithValidConfig_ReturnsTrue()
    {
        // Arrange - test with Connection String auth method
        var config = _databaseService.GetConfiguration();
        config.AuthMethod = AzureAuthMethod.ConnectionString;
        config.EncryptedConnectionString = new byte[] { 1, 2, 3 };
        config.PasswordSalt = new byte[] { 4, 5, 6 };
        _databaseService.SaveConfiguration(config);

        // Act
        var isConfigured = _databaseService.IsConfigured();

        // Assert
        Assert.True(isConfigured);
    }

    [Fact]
    public void IsConfigured_WithEntraIdConfig_ReturnsTrue()
    {
        // Arrange - test with Entra ID auth method
        var config = _databaseService.GetConfiguration();
        config.AuthMethod = AzureAuthMethod.EntraId;
        config.StorageAccountName = "teststorageaccount";
        config.IsEntraIdAuthenticated = true;
        config.PasswordSalt = new byte[] { 4, 5, 6 };
        _databaseService.SaveConfiguration(config);

        // Act
        var isConfigured = _databaseService.IsConfigured();

        // Assert
        Assert.True(isConfigured);
    }

    #endregion

    #region BackedUpFile Tests

    [Fact]
    public void SaveBackedUpFile_InsertsNewFile()
    {
        // Arrange
        var file = CreateTestBackedUpFile("C:\\test\\file.txt");

        // Act
        _databaseService.SaveBackedUpFile(file);
        var retrieved = _databaseService.GetBackedUpFile("C:\\test\\file.txt");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(file.LocalPath, retrieved.LocalPath);
        Assert.Equal(file.FileHash, retrieved.FileHash);
    }

    [Fact]
    public void SaveBackedUpFile_UpdatesExistingFile()
    {
        // Arrange
        var file = CreateTestBackedUpFile("C:\\test\\update.txt");
        _databaseService.SaveBackedUpFile(file);
        
        file.FileHash = "new-hash-value";
        file.Status = BackupStatus.Completed;

        // Act
        _databaseService.SaveBackedUpFile(file);
        var retrieved = _databaseService.GetBackedUpFile("C:\\test\\update.txt");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("new-hash-value", retrieved.FileHash);
        Assert.Equal(BackupStatus.Completed, retrieved.Status);
    }

    [Fact]
    public void GetBackedUpFile_NonExistent_ReturnsNull()
    {
        // Act
        var retrieved = _databaseService.GetBackedUpFile("C:\\nonexistent\\file.txt");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public void GetAllBackedUpFiles_ReturnsAllFiles()
    {
        // Arrange
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\file1.txt"));
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\file2.txt"));
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\file3.txt"));

        // Act
        var files = _databaseService.GetAllBackedUpFiles();

        // Assert
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void GetFilesByStatus_ReturnsFilteredFiles()
    {
        // Arrange
        var completed = CreateTestBackedUpFile("C:\\completed.txt");
        completed.Status = BackupStatus.Completed;
        var pending = CreateTestBackedUpFile("C:\\pending.txt");
        pending.Status = BackupStatus.Pending;
        
        _databaseService.SaveBackedUpFile(completed);
        _databaseService.SaveBackedUpFile(pending);

        // Act
        var completedFiles = _databaseService.GetFilesByStatus(BackupStatus.Completed);

        // Assert
        Assert.Single(completedFiles);
        Assert.Equal("C:\\completed.txt", completedFiles[0].LocalPath);
    }

    [Fact]
    public void DeleteBackedUpFile_RemovesFile()
    {
        // Arrange
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\delete.txt"));

        // Act
        _databaseService.DeleteBackedUpFile("C:\\delete.txt");
        var retrieved = _databaseService.GetBackedUpFile("C:\\delete.txt");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public void GetTotalBackedUpSize_ReturnsSumOfFileSizes()
    {
        // Arrange
        var file1 = CreateTestBackedUpFile("C:\\file1.txt");
        file1.FileSize = 1000;
        var file2 = CreateTestBackedUpFile("C:\\file2.txt");
        file2.FileSize = 2000;
        
        _databaseService.SaveBackedUpFile(file1);
        _databaseService.SaveBackedUpFile(file2);

        // Act
        var totalSize = _databaseService.GetTotalBackedUpSize();

        // Assert
        Assert.Equal(3000, totalSize);
    }

    #endregion

    #region Pending Changes Tests

    [Fact]
    public void QueueFileChange_AddsChange()
    {
        // Arrange
        FileChangeEvent change = new()
        {
            FilePath = "C:\\changed.txt",
            ChangeType = FileChangeType.Modified,
            DetectedAt = DateTime.UtcNow
        };

        // Act
        _databaseService.QueueFileChange(change);
        var pending = _databaseService.GetPendingChanges();

        // Assert
        Assert.Single(pending);
        Assert.Equal("C:\\changed.txt", pending[0].FilePath);
    }

    [Fact]
    public void QueueFileChange_ReplacesExistingChange()
    {
        // Arrange
        FileChangeEvent change1 = new()
        {
            FilePath = "C:\\file.txt",
            ChangeType = FileChangeType.Created,
            DetectedAt = DateTime.UtcNow
        };
        FileChangeEvent change2 = new()
        {
            FilePath = "C:\\file.txt",
            ChangeType = FileChangeType.Modified,
            DetectedAt = DateTime.UtcNow.AddSeconds(1)
        };

        // Act
        _databaseService.QueueFileChange(change1);
        _databaseService.QueueFileChange(change2);
        var pending = _databaseService.GetPendingChanges();

        // Assert
        Assert.Single(pending);
        Assert.Equal(FileChangeType.Modified, pending[0].ChangeType);
    }

    [Fact]
    public void GetPendingChanges_ReturnsOrderedByTime()
    {
        // Arrange
        FileChangeEvent change1 = new() { FilePath = "C:\\first.txt", DetectedAt = DateTime.UtcNow };
        FileChangeEvent change2 = new() { FilePath = "C:\\second.txt", DetectedAt = DateTime.UtcNow.AddMinutes(1) };
        FileChangeEvent change3 = new() { FilePath = "C:\\third.txt", DetectedAt = DateTime.UtcNow.AddMinutes(-1) };
        
        _databaseService.QueueFileChange(change1);
        _databaseService.QueueFileChange(change2);
        _databaseService.QueueFileChange(change3);

        // Act
        var pending = _databaseService.GetPendingChanges();

        // Assert
        Assert.Equal("C:\\third.txt", pending[0].FilePath);
        Assert.Equal("C:\\first.txt", pending[1].FilePath);
        Assert.Equal("C:\\second.txt", pending[2].FilePath);
    }

    [Fact]
    public void GetPendingChanges_RespectsBatchSize()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            _databaseService.QueueFileChange(new FileChangeEvent 
            { 
                FilePath = $"C:\\file{i}.txt",
                DetectedAt = DateTime.UtcNow.AddSeconds(i)
            });
        }

        // Act
        var pending = _databaseService.GetPendingChanges(batchSize: 5);

        // Assert
        Assert.Equal(5, pending.Count);
    }

    [Fact]
    public void RemovePendingChange_RemovesSpecificChange()
    {
        // Arrange
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\keep.txt" });
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\remove.txt" });

        // Act
        _databaseService.RemovePendingChange("C:\\remove.txt");
        var pending = _databaseService.GetPendingChanges();

        // Assert
        Assert.Single(pending);
        Assert.Equal("C:\\keep.txt", pending[0].FilePath);
    }

    [Fact]
    public void ClearPendingChanges_RemovesAllChanges()
    {
        // Arrange
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\file1.txt" });
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\file2.txt" });

        // Act
        _databaseService.ClearPendingChanges();

        // Assert
        Assert.Equal(0, _databaseService.GetPendingChangesCount());
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        var completed = CreateTestBackedUpFile("C:\\completed.txt");
        completed.Status = BackupStatus.Completed;
        completed.FileSize = 1000;
        
        var pending = CreateTestBackedUpFile("C:\\pending.txt");
        pending.Status = BackupStatus.Pending;
        pending.FileSize = 2000;
        
        _databaseService.SaveBackedUpFile(completed);
        _databaseService.SaveBackedUpFile(pending);
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\change.txt" });

        // Act
        var stats = _databaseService.GetStatistics();

        // Assert
        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(3000, stats.TotalSize);
        Assert.Equal(1, stats.CompletedFiles);
        Assert.Equal(1, stats.PendingFiles);
        Assert.Equal(1, stats.PendingChanges);
    }

    [Fact]
    public void GetStatistics_TotalSizeFormatted_FormatsCorrectly()
    {
        // Arrange
        var file = CreateTestBackedUpFile("C:\\large.bin");
        file.FileSize = 1536 * 1024 * 1024; // 1.5 GB
        _databaseService.SaveBackedUpFile(file);

        // Act
        var stats = _databaseService.GetStatistics();

        // Assert
        Assert.Contains("GB", stats.TotalSizeFormatted);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentWrites_DoNotCorruptData()
    {
        // Arrange
        List<Task> tasks = new();
        var fileCount = 100;

        // Act - Write files concurrently
        for (int i = 0; i < fileCount; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                _databaseService.SaveBackedUpFile(CreateTestBackedUpFile($"C:\\concurrent{index}.txt"));
            }));
        }
        await Task.WhenAll(tasks);

        // Assert
        var files = _databaseService.GetAllBackedUpFiles();
        Assert.Equal(fileCount, files.Count);
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotDeadlock()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        List<Task> tasks = new();

        // Act - Mix of reads and writes
        for (int i = 0; i < 50; i++)
        {
            var index = i;
            if (index % 2 == 0)
            {
                tasks.Add(Task.Run(() =>
                {
                    _databaseService.SaveBackedUpFile(CreateTestBackedUpFile($"C:\\file{index}.txt"));
                }));
            }
            else
            {
                tasks.Add(Task.Run(() =>
                {
                    _databaseService.GetAllBackedUpFiles();
                    _databaseService.GetStatistics();
                }));
            }
        }

        // Assert - Should complete without deadlock
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Helper Methods

    private static BackedUpFile CreateTestBackedUpFile(string path)
    {
        return new BackedUpFile
        {
            LocalPath = path,
            FileSize = 1024,
            LastModified = DateTime.UtcNow,
            FileHash = Guid.NewGuid().ToString("N"),
            Status = BackupStatus.Pending,
            BackedUpAt = DateTime.UtcNow
        };
    }

    #endregion

    #region SecureReset Tests

    [Fact]
    public void SecureReset_ClearsAllData()
    {
        // Arrange - Add some data
        var config = _databaseService.GetConfiguration();
        config.ContainerName = "test-container";
        config.PasswordSalt = new byte[] { 1, 2, 3, 4 };
        config.StorageAccountName = "teststorageaccount";
        config.IsEntraIdAuthenticated = true;
        _databaseService.SaveConfiguration(config);
        
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\file1.txt"));
        _databaseService.QueueFileChange(new FileChangeEvent { FilePath = "C:\\change.txt" });
        
        // Act
        _databaseService.SecureReset();
        
        // Assert
        var newConfig = _databaseService.GetConfiguration();
        Assert.Null(newConfig.PasswordSalt);
        Assert.Null(newConfig.StorageAccountName);
        Assert.False(newConfig.IsEntraIdAuthenticated);
        Assert.Empty(_databaseService.GetAllBackedUpFiles());
        Assert.Equal(0, _databaseService.GetPendingChangesCount());
    }

    [Fact]
    public void SecureReset_DatabaseRemainsUsable()
    {
        // Arrange
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\old.txt"));
        
        // Act
        _databaseService.SecureReset();
        
        // Can still use the database after reset
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\new.txt"));
        
        // Assert
        var files = _databaseService.GetAllBackedUpFiles();
        Assert.Single(files);
        Assert.Equal("C:\\new.txt", files[0].LocalPath);
    }

    [Fact]
    public void DatabasePath_ReturnsCorrectPath()
    {
        // Assert
        Assert.Equal(_dbPath, _databaseService.DatabasePath);
    }

    #endregion
}
