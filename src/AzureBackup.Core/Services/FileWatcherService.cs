using System.Collections.Concurrent;
using System.Diagnostics;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Monitors file system changes in watched folders and queues them for backup.
/// Handles debouncing to avoid processing rapid successive changes.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly LocalDatabaseService _databaseService;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private bool _disposed;
    private bool _isRunning;

    // Debounce delay to batch rapid changes
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);

    public event EventHandler<FileChangeEvent>? FileChanged;
    public event EventHandler<string>? Error;
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [FileWatcher] {message}");
    }

    public bool IsRunning => _isRunning;

    public FileWatcherService(LocalDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    /// <summary>
    /// Starts watching all configured folders.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        Log("Start: Starting file watcher service");

        var config = _databaseService.GetConfiguration();
        
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            AddWatcher(folder);
        }

        _debounceTimer = new Timer(ProcessPendingChanges, null, _debounceDelay, _debounceDelay);
        _isRunning = true;
        Log($"Start: Now watching {_watchers.Count} folders");
    }

    /// <summary>
    /// Stops all file watchers.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }

        _isRunning = false;
    }

    /// <summary>
    /// Adds a watcher for a specific folder.
    /// </summary>
    public void AddWatcher(WatchedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        
        if (!Directory.Exists(folder.Path))
        {
            Error?.Invoke(this, $"Folder does not exist: {folder.Path}");
            return;
        }

        lock (_lock)
        {
            if (_watchers.ContainsKey(folder.Path))
                return;

            FileSystemWatcher watcher = new(folder.Path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // 64KB buffer to handle burst changes
                NotifyFilter = NotifyFilters.FileName | 
                               NotifyFilters.DirectoryName | 
                               NotifyFilters.LastWrite | 
                               NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) => OnFileChanged(e.FullPath, FileChangeType.Created, folder);
            watcher.Changed += (s, e) => OnFileChanged(e.FullPath, FileChangeType.Modified, folder);
            watcher.Deleted += (s, e) => OnFileChanged(e.FullPath, FileChangeType.Deleted, folder);
            watcher.Renamed += (s, e) =>
            {
                OnFileChanged(e.OldFullPath, FileChangeType.Deleted, folder);
                OnFileChanged(e.FullPath, FileChangeType.Created, folder);
            };
            watcher.Error += (s, e) => 
            {
                var ex = e.GetException();
                if (ex is InternalBufferOverflowException)
                {
                    // Buffer overflow - queue a full rescan of this folder
                    Error?.Invoke(this, $"File watcher buffer overflow for {folder.Path} - some changes may be missed");
                    QueueFolderRescan(folder);
                }
                else
                {
                    Error?.Invoke(this, ex.Message);
                }
            };

            _watchers[folder.Path] = watcher;
        }
    }
    
    /// <summary>
    /// Queues a folder for rescan after buffer overflow.
    /// </summary>
    private void QueueFolderRescan(WatchedFolder folder)
    {
        // Queue all files in the folder as potentially changed
        try
        {
            var files = Directory.EnumerateFiles(folder.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            });

            foreach (var file in files)
            {
                _pendingChanges[file] = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to rescan folder {folder.Path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a watcher for a specific folder.
    /// </summary>
    public void RemoveWatcher(string folderPath)
    {
        lock (_lock)
        {
            if (_watchers.TryGetValue(folderPath, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _watchers.Remove(folderPath);
            }
        }
    }

    /// <summary>
    /// Scans a folder and returns all files that need to be backed up.
    /// </summary>
    public async Task<List<string>> ScanFolderAsync(WatchedFolder folder, CancellationToken cancellationToken = default)
    {
        List<string> files = new();
        var config = _databaseService.GetConfiguration();

        await Task.Run(() =>
        {
            try
            {
                var allFiles = Directory.EnumerateFiles(folder.Path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                });

                foreach (var file in allFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ShouldExclude(file, folder, config.GlobalExcludePatterns))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Error scanning folder {folder.Path}: {ex.Message}");
            }
        }, cancellationToken);

        return files;
    }

    /// <summary>
    /// Checks if a file is currently locked/in use.
    /// </summary>
    public static bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Waits for a file to become available (unlocked).
    /// </summary>
    public static async Task<bool> WaitForFileAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var endTime = DateTime.UtcNow + timeout;
        
        while (DateTime.UtcNow < endTime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (!IsFileLocked(filePath))
                return true;
            
            await Task.Delay(500, cancellationToken);
        }
        
        return false;
    }

    private void OnFileChanged(string filePath, FileChangeType changeType, WatchedFolder folder)
    {
        try
        {
            var config = _databaseService.GetConfiguration();

            // Skip if file should be excluded
            if (ShouldExclude(filePath, folder, config.GlobalExcludePatterns))
                return;

            // Skip directories
            if (changeType != FileChangeType.Deleted && Directory.Exists(filePath))
                return;

            // Add to pending changes (debounced)
            _pendingChanges[filePath] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Error processing file change: {ex.Message}");
        }
    }

    private void ProcessPendingChanges(object? state)
    {
        var cutoff = DateTime.UtcNow - _debounceDelay;
        List<string> toProcess = new();

        foreach (var kvp in _pendingChanges)
        {
            if (kvp.Value <= cutoff)
            {
                toProcess.Add(kvp.Key);
            }
        }

        foreach (var filePath in toProcess)
        {
            if (_pendingChanges.TryRemove(filePath, out _))
            {
                var changeType = File.Exists(filePath) 
                    ? FileChangeType.Modified 
                    : FileChangeType.Deleted;

                FileChangeEvent changeEvent = new()
                {
                    FilePath = filePath,
                    ChangeType = changeType,
                    DetectedAt = DateTime.UtcNow
                };

                _databaseService.QueueFileChange(changeEvent);
                FileChanged?.Invoke(this, changeEvent);
            }
        }
    }

    private bool ShouldExclude(string filePath, WatchedFolder folder, List<string> globalPatterns)
    {
        var fileName = Path.GetFileName(filePath);
        var relativePath = Path.GetRelativePath(folder.Path, filePath);

        // Check global exclude patterns
        foreach (var pattern in globalPatterns)
        {
            if (GlobMatcher.IsMatch(fileName, pattern) || GlobMatcher.IsMatch(relativePath, pattern))
                return true;
        }

        // Check folder-specific exclude patterns
        foreach (var pattern in folder.ExcludePatterns)
        {
            if (GlobMatcher.IsMatch(fileName, pattern) || GlobMatcher.IsMatch(relativePath, pattern))
                return true;
        }

        // Check excluded subfolders
        foreach (var excludedSubfolder in folder.ExcludeSubfolders)
        {
            var normalizedExclude = excludedSubfolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relativePath.StartsWith(normalizedExclude, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Skip common system/temp files
        var systemPatterns = new[]
        {
            "*.tmp", "*.temp", "~*", "*.lock", "thumbs.db", "desktop.ini",
            ".DS_Store", "*.swp", "*.bak"
        };

        if (GlobMatcher.MatchesAny(fileName, systemPatterns))
            return true;

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
