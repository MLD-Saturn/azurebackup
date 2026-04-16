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
    private const string TestPassword = "TestPassword123!";

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        
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
    public void Initialize_WithNullPath_ThrowsArgumentException()
    {
        // Arrange
        using LocalDatabaseService service = new();

        // Act & Assert - ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        Assert.ThrowsAny<ArgumentException>(() => service.Initialize(null!, TestPassword));
    }

    [Fact]
    public void Initialize_WithNullPassword_ThrowsArgumentException()
    {
        // Arrange
        using LocalDatabaseService service = new();
        var testPath = Path.Combine(_testDirectory, "nullpwd.db");

        // Act & Assert - ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        Assert.ThrowsAny<ArgumentException>(() => service.Initialize(testPath, null!));
    }

    [Fact]
    public void Initialize_CreatesDirectoryIfNeeded()
    {
        // Arrange
        using LocalDatabaseService service = new();
        var nestedPath = Path.Combine(_testDirectory, "nested", "deep", "db.db");

        // Act
        service.Initialize(nestedPath, TestPassword);

        // Assert
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Initialize_WithWrongPassword_ThrowsInvalidPasswordException()
    {
        // Arrange - first create a database with a password
        var encryptedDbPath = Path.Combine(_testDirectory, "encrypted.db");
        using (var service1 = new LocalDatabaseService())
        {
            service1.Initialize(encryptedDbPath, "CorrectPassword");
        }

        // Act & Assert - try to open with wrong password
        using var service2 = new LocalDatabaseService();
        Assert.Throws<AzureBackup.Core.InvalidPasswordException>(() => 
            service2.Initialize(encryptedDbPath, "WrongPassword"));
    }

    #endregion

    #region Migration Tests

    [Fact]
    public void DatabaseExists_WhenFileExists_ReturnsTrue()
    {
        // Assert - database was created in InitializeAsync
        Assert.True(LocalDatabaseService.DatabaseExists(_dbPath));
    }

    [Fact]
    public void DatabaseExists_WhenFileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");

        // Act & Assert
        Assert.False(LocalDatabaseService.DatabaseExists(nonExistentPath));
    }

    [Fact]
    public void IsUnencryptedDatabase_WithEncryptedDatabase_ReturnsFalse()
    {
        // Assert - our test database is encrypted
        Assert.False(LocalDatabaseService.IsUnencryptedDatabase(_dbPath));
    }

    [Fact]
    public void IsUnencryptedDatabase_WithNonExistentFile_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");

        // Act & Assert
        Assert.False(LocalDatabaseService.IsUnencryptedDatabase(nonExistentPath));
    }

    [Fact]
    public void MigrateToEncrypted_WithNonExistentSource_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");
        var targetPath = Path.Combine(_testDirectory, "target.db");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => 
            LocalDatabaseService.MigrateToEncrypted(nonExistentPath, targetPath, "Password123!"));
    }

    [Fact]
    public void MigrateToEncrypted_WithNullArguments_ThrowsArgumentException()
    {
        // Act & Assert - ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        Assert.ThrowsAny<ArgumentException>(() => 
            LocalDatabaseService.MigrateToEncrypted(null!, "target.db", "password"));
        
        Assert.ThrowsAny<ArgumentException>(() => 
            LocalDatabaseService.MigrateToEncrypted("source.db", null!, "password"));
        
        Assert.ThrowsAny<ArgumentException>(() => 
            LocalDatabaseService.MigrateToEncrypted("source.db", "target.db", null!));
    }

    [Fact]
    public void IsLegacyEncryptedDatabase_WithNewArgon2idDatabase_ReturnsFalse()
    {
        // The test fixture database is created with Argon2id (new format)
        Assert.False(LocalDatabaseService.IsLegacyEncryptedDatabase(_dbPath));
    }

    [Fact]
    public void IsLegacyEncryptedDatabase_WithNonExistentFile_ReturnsFalse()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");
        Assert.False(LocalDatabaseService.IsLegacyEncryptedDatabase(nonExistentPath));
    }

    [Fact]
    public void HasArgon2idSalt_WithNewDatabase_ReturnsTrue()
    {
        // The test fixture database has a salt file
        Assert.True(LocalDatabaseService.HasArgon2idSalt(_dbPath));
    }

    [Fact]
    public void HasArgon2idSalt_WithNonExistentDatabase_ReturnsFalse()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");
        Assert.False(LocalDatabaseService.HasArgon2idSalt(nonExistentPath));
    }

    [Fact]
    public void MigrateLegacyEncrypted_MigratesDataSuccessfully()
    {
        // Arrange - Create a "legacy" encrypted database (simulate old format by creating without salt file)
        var legacyPath = Path.Combine(_testDirectory, "legacy.db");
        var upgradedPath = Path.Combine(_testDirectory, "upgraded.db");
        var testPassword = "LegacyPassword123!";
        
        // Create a database with raw password (simulating legacy format)
        var legacyConnString = new LiteDB.ConnectionString
        {
            Filename = legacyPath,
            Password = testPassword, // Raw password - legacy method
            Connection = LiteDB.ConnectionType.Shared
        };
        
        using (var legacyDb = new LiteDB.LiteDatabase(legacyConnString))
        {
            // Add some test data
            var configCollection = legacyDb.GetCollection<AzureBackup.Core.Models.BackupConfiguration>("config");
            configCollection.Insert(new AzureBackup.Core.Models.BackupConfiguration
            {
                Id = 1,
                ContainerName = "legacy-container",
                StorageAccountName = "legacyaccount"
            });
        }
        
        // Verify it's detected as legacy (no salt file)
        Assert.True(LocalDatabaseService.IsLegacyEncryptedDatabase(legacyPath));
        Assert.False(LocalDatabaseService.HasArgon2idSalt(legacyPath));

        // Act - migrate to new Argon2id format
        LocalDatabaseService.MigrateLegacyEncrypted(legacyPath, upgradedPath, testPassword);

        // Assert - verify upgraded database has the data and uses Argon2id
        Assert.True(LocalDatabaseService.HasArgon2idSalt(upgradedPath));
        Assert.False(LocalDatabaseService.IsLegacyEncryptedDatabase(upgradedPath));
        
        using var upgradedService = new LocalDatabaseService();
        upgradedService.Initialize(upgradedPath, testPassword);
        
        var migratedConfig = upgradedService.GetConfiguration();
        Assert.Equal("legacy-container", migratedConfig.ContainerName);
        Assert.Equal("legacyaccount", migratedConfig.StorageAccountName);
    }

    [Fact]
    public void MigrateLegacyEncrypted_WithWrongPassword_ThrowsInvalidPasswordException()
    {
        // Arrange - Create a "legacy" encrypted database
        var legacyPath = Path.Combine(_testDirectory, "legacy_wrong_pwd.db");
        var upgradedPath = Path.Combine(_testDirectory, "upgraded_wrong_pwd.db");
        var correctPassword = "CorrectPassword123!";
        var wrongPassword = "WrongPassword123!";
        
        // Create a database with raw password
        var legacyConnString = new LiteDB.ConnectionString
        {
            Filename = legacyPath,
            Password = correctPassword,
            Connection = LiteDB.ConnectionType.Shared
        };
        
        using (var legacyDb = new LiteDB.LiteDatabase(legacyConnString))
        {
            var configCollection = legacyDb.GetCollection<AzureBackup.Core.Models.BackupConfiguration>("config");
            configCollection.Insert(new AzureBackup.Core.Models.BackupConfiguration { Id = 1 });
        }

        // Act & Assert - should throw InvalidPasswordException
        Assert.Throws<AzureBackup.Core.InvalidPasswordException>(() =>
            LocalDatabaseService.MigrateLegacyEncrypted(legacyPath, upgradedPath, wrongPassword));
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
    public void QueueFileChangesBatch_InsertsAllEntries()
    {
        // Arrange
        var batch = new List<FileChangeEvent>
        {
            new() { FilePath = "C:\\batch-a.txt", ChangeType = FileChangeType.Created, DetectedAt = DateTime.UtcNow },
            new() { FilePath = "C:\\batch-b.txt", ChangeType = FileChangeType.Modified, DetectedAt = DateTime.UtcNow },
            new() { FilePath = "C:\\batch-c.txt", ChangeType = FileChangeType.Deleted, DetectedAt = DateTime.UtcNow }
        };

        // Act
        _databaseService.QueueFileChangesBatch(batch);
        var pending = _databaseService.GetPendingChanges();

        // Assert
        Assert.Equal(3, pending.Count);
        Assert.Contains(pending, p => p.FilePath == "C:\\batch-a.txt" && p.ChangeType == FileChangeType.Created);
        Assert.Contains(pending, p => p.FilePath == "C:\\batch-b.txt" && p.ChangeType == FileChangeType.Modified);
        Assert.Contains(pending, p => p.FilePath == "C:\\batch-c.txt" && p.ChangeType == FileChangeType.Deleted);
    }

    [Fact]
    public void QueueFileChangesBatch_ReplacesExistingEntriesForSamePath()
    {
        // Arrange: seed an existing pending change
        _databaseService.QueueFileChange(new FileChangeEvent
        {
            FilePath = "C:\\reused.txt",
            ChangeType = FileChangeType.Created,
            DetectedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var batch = new List<FileChangeEvent>
        {
            new() { FilePath = "C:\\reused.txt", ChangeType = FileChangeType.Modified, DetectedAt = DateTime.UtcNow },
            new() { FilePath = "C:\\new.txt", ChangeType = FileChangeType.Created, DetectedAt = DateTime.UtcNow }
        };

        // Act
        _databaseService.QueueFileChangesBatch(batch);
        var pending = _databaseService.GetPendingChanges();

        // Assert: existing row for C:\reused.txt is replaced; second row is added
        Assert.Equal(2, pending.Count);
        var reused = Assert.Single(pending.Where(p => p.FilePath == "C:\\reused.txt"));
        Assert.Equal(FileChangeType.Modified, reused.ChangeType);
        Assert.Contains(pending, p => p.FilePath == "C:\\new.txt");
    }

    [Fact]
    public void QueueFileChangesBatch_LastDuplicateWins()
    {
        // Arrange: two entries for the same path in one batch
        var batch = new List<FileChangeEvent>
        {
            new() { FilePath = "C:\\dup.txt", ChangeType = FileChangeType.Created, DetectedAt = DateTime.UtcNow.AddMinutes(-1) },
            new() { FilePath = "C:\\dup.txt", ChangeType = FileChangeType.Modified, DetectedAt = DateTime.UtcNow }
        };

        // Act
        _databaseService.QueueFileChangesBatch(batch);
        var pending = _databaseService.GetPendingChanges();

        // Assert: only one row, the later-added entry wins
        var single = Assert.Single(pending);
        Assert.Equal("C:\\dup.txt", single.FilePath);
        Assert.Equal(FileChangeType.Modified, single.ChangeType);
    }

    [Fact]
    public void QueueFileChangesBatch_EmptyBatch_IsNoOp()
    {
        // Act
        _databaseService.QueueFileChangesBatch(Array.Empty<FileChangeEvent>());

        // Assert
        Assert.Empty(_databaseService.GetPendingChanges());
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
        // Arrange
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
        
        // Assert - Database is closed after SecureReset, re-initialize to verify data is cleared
        Assert.False(_databaseService.IsInitialized);
        Assert.False(File.Exists(_dbPath));
        
        // Re-initialize with new password to verify clean state
        _databaseService.Initialize(_dbPath, TestPassword);
        
        var newConfig = _databaseService.GetConfiguration();
        Assert.Null(newConfig.PasswordSalt);
        Assert.Null(newConfig.StorageAccountName);
        Assert.False(newConfig.IsEntraIdAuthenticated);
        Assert.Empty(_databaseService.GetAllBackedUpFiles());
        Assert.Empty(_databaseService.GetPendingChanges(1));
    }

    [Fact]
    public void SecureReset_DatabaseRemainsUsable()
    {
        // Arrange
        _databaseService.SaveBackedUpFile(CreateTestBackedUpFile("C:\\old.txt"));
        
        // Act
        _databaseService.SecureReset();
        
        // Assert - Database must be re-initialized after SecureReset
        Assert.False(_databaseService.IsInitialized);
        
        // Re-initialize and verify we can use it
        _databaseService.Initialize(_dbPath, TestPassword);
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
