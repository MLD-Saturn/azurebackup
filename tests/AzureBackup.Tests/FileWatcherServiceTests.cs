using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for FileWatcherService, particularly the pattern matching and exclusion logic.
/// These tests verify file exclusion behavior and can catch regressions in pattern matching.
/// </summary>
public class FileWatcherServiceTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly LocalDatabaseService _databaseService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly string _testFolder;

    public FileWatcherServiceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_filewatcher_{Guid.NewGuid()}.db");
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_tempDbPath);
        _fileWatcherService = new FileWatcherService(_databaseService);
        _testFolder = Path.Combine(Path.GetTempPath(), $"test_watch_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testFolder);
    }

    public void Dispose()
    {
        _fileWatcherService.Dispose();
        _databaseService.Dispose();
        
        if (File.Exists(_tempDbPath))
            File.Delete(_tempDbPath);
        
        if (Directory.Exists(_testFolder))
            Directory.Delete(_testFolder, true);
        
        GC.SuppressFinalize(this);
    }

    #region File Extension Pattern Tests

    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.TXT", true)] // Case insensitive
    [InlineData("*.txt", "file.doc", false)]
    [InlineData("*.log", "debug.log", true)]
    [InlineData("*.tmp", "temp.tmp", true)]
    [InlineData("~*", "~tempfile", true)]
    [InlineData("~*", "normal_file", false)]
    [InlineData("thumbs.db", "thumbs.db", true)]
    [InlineData("thumbs.db", "Thumbs.db", true)] // Case insensitive
    [InlineData("thumbs.db", "thumbs.db.bak", true)] // Pattern matches substring due to glob behavior
    public async Task ScanFolderAsync_WithExcludePatterns_ExcludesMatchingFiles(
        string pattern, string fileName, bool shouldExclude)
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, fileName);
        await File.WriteAllTextAsync(filePath, "test content");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = [pattern]
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert
        var wasExcluded = !files.Any(f => Path.GetFileName(f) == fileName);
        Assert.Equal(shouldExclude, wasExcluded);
    }

    [Fact]
    public async Task ScanFolderAsync_SystemFilesAlwaysExcluded()
    {
        // Arrange - create system files that should always be excluded
        var systemFiles = new[] { "file.tmp", "file.temp", "~lockfile", "thumbs.db", "desktop.ini", ".DS_Store" };
        
        foreach (var fileName in systemFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(_testFolder, fileName), "test");
        }

        // Also create a normal file
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "normal.txt"), "test");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = []
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert - only normal.txt should be included
        Assert.Single(files);
        Assert.Contains("normal.txt", files[0]);
    }

    [Fact]
    public async Task ScanFolderAsync_MultiplePatterns_ExcludesAll()
    {
        // Arrange
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "app.log"), "log content");
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "debug.txt"), "debug content");
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "important.doc"), "doc content");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = ["*.log", "*.txt"]
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert - only .doc file should remain
        Assert.Single(files);
        Assert.Contains("important.doc", files[0]);
    }

    #endregion

    #region Subfolder Exclusion Tests

    [Fact]
    public async Task ScanFolderAsync_ExcludedSubfolders_ExcludesEntireDirectory()
    {
        // Arrange
        var subFolder = Path.Combine(_testFolder, "excluded_folder");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(subFolder, "file_in_excluded.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "root_file.txt"), "content");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = [],
            ExcludeSubfolders = ["excluded_folder"]
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert - only root file should be included
        Assert.Single(files);
        Assert.Contains("root_file.txt", files[0]);
    }

    [Fact]
    public async Task ScanFolderAsync_NodeModulesExcluded_ViaExcludeSubfolders()
    {
        // Arrange - this is how node_modules should be excluded (via ExcludeSubfolders)
        var nodeModules = Path.Combine(_testFolder, "node_modules");
        Directory.CreateDirectory(nodeModules);
        await File.WriteAllTextAsync(Path.Combine(nodeModules, "package.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_testFolder, "index.js"), "content");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = [],
            ExcludeSubfolders = ["node_modules"]
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert - only index.js should be included
        Assert.Single(files);
        Assert.Contains("index.js", files[0]);
    }

    [Fact]
    public async Task ScanFolderAsync_RecursiveSubdirectories_FindsAllFiles()
    {
        // Arrange
        var subFolder1 = Path.Combine(_testFolder, "sub1");
        var subFolder2 = Path.Combine(_testFolder, "sub1", "sub2");
        Directory.CreateDirectory(subFolder2);

        await File.WriteAllTextAsync(Path.Combine(_testFolder, "root.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subFolder1, "level1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subFolder2, "level2.txt"), "content");

        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true,
            ExcludePatterns = []
        };

        // Act
        var files = await _fileWatcherService.ScanFolderAsync(folder, CancellationToken.None);

        // Assert
        Assert.Equal(3, files.Count);
    }

    #endregion

    #region File Lock Detection Tests

    [Fact]
    public void IsFileLocked_UnlockedFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "unlocked.txt");
        File.WriteAllText(filePath, "content");

        // Act
        var isLocked = FileWatcherService.IsFileLocked(filePath);

        // Assert
        Assert.False(isLocked);
    }

    [Fact]
    public void IsFileLocked_LockedFile_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "locked.txt");
        File.WriteAllText(filePath, "content");

        // Lock the file with exclusive access
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var isLocked = FileWatcherService.IsFileLocked(filePath);

        // Assert
        Assert.True(isLocked);
    }

    [Fact]
    public void IsFileLocked_NonExistentFile_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "nonexistent.txt");

        // Act
        var isLocked = FileWatcherService.IsFileLocked(filePath);

        // Assert - treats non-existent as "locked" (not accessible)
        Assert.True(isLocked);
    }

    [Fact]
    public async Task WaitForFileAsync_UnlockedFile_ReturnsImmediately()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "unlocked.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await FileWatcherService.WaitForFileAsync(filePath, TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        // Assert
        Assert.True(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000); // Should be nearly instant
    }

    [Fact]
    public async Task WaitForFileAsync_LockedFileThenUnlocked_ReturnsWhenUnlocked()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "will_unlock.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Lock the file
        FileStream lockStream = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Start waiting in background
        var waitTask = FileWatcherService.WaitForFileAsync(filePath, TimeSpan.FromSeconds(5));

        // Unlock after 1 second
        await Task.Delay(1000);
        await lockStream.DisposeAsync();

        // Act
        var result = await waitTask;

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForFileAsync_Timeout_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "always_locked.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Lock the file for the duration
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = await FileWatcherService.WaitForFileAsync(filePath, TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WaitForFileAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var filePath = Path.Combine(_testFolder, "cancelled.txt");
        await File.WriteAllTextAsync(filePath, "content");

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource cts = new(500);

        // Act & Assert - TaskCanceledException is a subclass of OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileWatcherService.WaitForFileAsync(filePath, TimeSpan.FromSeconds(10), cts.Token));
    }

    #endregion

    #region Watcher Lifecycle Tests

    [Fact]
    public void Start_WithValidFolder_StartsWatching()
    {
        // Arrange
        var config = _databaseService.GetConfiguration();
        config.WatchedFolders.Add(new WatchedFolder
        {
            Path = _testFolder,
            IsEnabled = true
        });
        _databaseService.SaveConfiguration(config);

        // Act
        _fileWatcherService.Start();

        // Assert
        Assert.True(_fileWatcherService.IsRunning);
    }

    [Fact]
    public void Stop_WhenRunning_StopsWatching()
    {
        // Arrange
        var config = _databaseService.GetConfiguration();
        config.WatchedFolders.Add(new WatchedFolder
        {
            Path = _testFolder,
            IsEnabled = true
        });
        _databaseService.SaveConfiguration(config);
        _fileWatcherService.Start();

        // Act
        _fileWatcherService.Stop();

        // Assert
        Assert.False(_fileWatcherService.IsRunning);
    }

    [Fact]
    public void AddWatcher_NonExistentFolder_RaisesError()
    {
        // Arrange
        var errorRaised = false;
        _fileWatcherService.Error += (s, e) => errorRaised = true;

        WatchedFolder folder = new()
        {
            Path = @"C:\NonExistent\Path\That\Does\Not\Exist",
            IsEnabled = true
        };

        // Act
        _fileWatcherService.AddWatcher(folder);

        // Assert
        Assert.True(errorRaised);
    }

    [Fact]
    public async Task ScanFolderAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        WatchedFolder folder = new()
        {
            Path = _testFolder,
            IsEnabled = true
        };

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        // Act & Assert - TaskCanceledException is a subclass of OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fileWatcherService.ScanFolderAsync(folder, cts.Token));
    }

    #endregion
}
