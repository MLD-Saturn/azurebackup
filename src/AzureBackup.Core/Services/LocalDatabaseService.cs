using System.Security.Cryptography;
using AzureBackup.Core.Models;
using LiteDB;

namespace AzureBackup.Core.Services;

/// <summary>
/// Manages local database for tracking backup state, configuration, and file metadata.
/// Uses LiteDB for embedded, portable storage (no installation required).
/// </summary>
public class LocalDatabaseService : IDisposable
{
    private LiteDatabase? _database;
    private ILiteCollection<BackupConfiguration>? _configCollection;
    private ILiteCollection<BackedUpFile>? _filesCollection;
    private ILiteCollection<FileChangeEvent>? _pendingChangesCollection;
    private readonly object _dbLock = new();
    private bool _disposed;
    private string? _databasePath;

    public bool IsInitialized => _database != null;
    
    /// <summary>
    /// Gets the current database file path.
    /// </summary>
    public string? DatabasePath => _databasePath;
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Database] {message}");
    }

    /// <summary>
    /// Initializes the database at the specified path.
    /// </summary>
    public void Initialize(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Log($"Initialize: Opening database at {databasePath}");
        
        _databasePath = databasePath;
        
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            Log($"Initialize: Created directory {directory}");
        }

        _database = new LiteDatabase(databasePath);
        
        _configCollection = _database.GetCollection<BackupConfiguration>("config");
        _filesCollection = _database.GetCollection<BackedUpFile>("files");
        _pendingChangesCollection = _database.GetCollection<FileChangeEvent>("pending_changes");

        // Create indexes for faster queries
        _filesCollection.EnsureIndex(x => x.LocalPath, unique: true);
        _filesCollection.EnsureIndex(x => x.Status);
        _filesCollection.EnsureIndex(x => x.FileHash);
        Log("Initialize: Database initialized successfully");
    }

    #region Configuration

    /// <summary>
    /// Gets or creates the backup configuration.
    /// </summary>
    public BackupConfiguration GetConfiguration()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            var config = _configCollection!.FindById(1);
            if (config == null)
            {
                config = new BackupConfiguration { Id = 1 };
                _configCollection.Insert(config);
            }
            return config;
        }
    }

    /// <summary>
    /// Saves the backup configuration using a transaction.
    /// </summary>
    public void SaveConfiguration(BackupConfiguration config)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(config);
        
        lock (_dbLock)
        {
            _database!.BeginTrans();
            try
            {
                config.Id = 1;
                _configCollection!.Upsert(config);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Checks if the application has been configured with Azure credentials.
    /// </summary>
    public bool IsConfigured()
    {
        var config = GetConfiguration();
        // Check for either authentication method
        bool hasAzureConfig = config.AuthMethod == AzureAuthMethod.EntraId
            ? (!string.IsNullOrEmpty(config.StorageAccountName) && config.IsEntraIdAuthenticated)
            : (config.EncryptedConnectionString != null);
        
        return hasAzureConfig && config.PasswordSalt != null;
    }

    #endregion

    #region Backed Up Files

    /// <summary>
    /// Gets a backed up file by its local path.
    /// </summary>
    public BackedUpFile? GetBackedUpFile(string localPath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        
        lock (_dbLock)
        {
            return _filesCollection!.FindOne(x => x.LocalPath == localPath);
        }
    }

    /// <summary>
    /// Saves or updates a backed up file record using a transaction.
    /// </summary>
    public void SaveBackedUpFile(BackedUpFile file)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(file);
        
        lock (_dbLock)
        {
            _database!.BeginTrans();
            try
            {
                var existing = _filesCollection!.FindOne(x => x.LocalPath == file.LocalPath);
                if (existing != null)
                {
                    file.Id = existing.Id;
                    _filesCollection.Update(file);
                }
                else
                {
                    _filesCollection.Insert(file);
                }
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Gets all backed up files.
    /// </summary>
    public List<BackedUpFile> GetAllBackedUpFiles()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _filesCollection!.FindAll().ToList();
        }
    }

    /// <summary>
    /// Gets files with a specific backup status.
    /// </summary>
    public List<BackedUpFile> GetFilesByStatus(BackupStatus status)
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _filesCollection!.Find(x => x.Status == status).ToList();
        }
    }

    /// <summary>
    /// Gets the count of files by status.
    /// </summary>
    public Dictionary<BackupStatus, int> GetFileStatusCounts()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _filesCollection!
                .FindAll()
                .GroupBy(x => x.Status)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Deletes a backed up file record.
    /// </summary>
    public void DeleteBackedUpFile(string localPath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        
        lock (_dbLock)
        {
            _filesCollection!.DeleteMany(x => x.LocalPath == localPath);
        }
    }

    /// <summary>
    /// Gets total size of all backed up files.
    /// </summary>
    public long GetTotalBackedUpSize()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _filesCollection!.FindAll().Sum(x => x.FileSize);
        }
    }

    #endregion

    #region Pending Changes Queue

    /// <summary>
    /// Adds a file change to the pending queue.
    /// </summary>
    public void QueueFileChange(FileChangeEvent change)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(change);
        
        lock (_dbLock)
        {
            _database!.BeginTrans();
            try
            {
                // Remove any existing pending change for the same file
                _pendingChangesCollection!.DeleteMany(x => x.FilePath == change.FilePath);
                _pendingChangesCollection.Insert(change);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Gets the next batch of pending changes.
    /// </summary>
    public List<FileChangeEvent> GetPendingChanges(int batchSize = 100)
    {
        EnsureInitialized();
        if (batchSize <= 0) batchSize = 100;
        
        lock (_dbLock)
        {
            return _pendingChangesCollection!
                .FindAll()
                .OrderBy(x => x.DetectedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    /// <summary>
    /// Removes a pending change after it's been processed.
    /// </summary>
    public void RemovePendingChange(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        
        lock (_dbLock)
        {
            _pendingChangesCollection!.DeleteMany(x => x.FilePath == filePath);
        }
    }

    /// <summary>
    /// Gets count of pending changes.
    /// </summary>
    public int GetPendingChangesCount()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _pendingChangesCollection!.Count();
        }
    }

    /// <summary>
    /// Checks if a file change is already pending in the queue.
    /// </summary>
    public bool IsFileChangePending(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        
        lock (_dbLock)
        {
            return _pendingChangesCollection!.Exists(x => x.FilePath == filePath);
        }
    }

    /// <summary>
    /// Clears all pending changes.
    /// </summary>
    public void ClearPendingChanges()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            _pendingChangesCollection!.DeleteAll();
        }
    }

    /// <summary>
    /// Removes pending changes for files that are already backed up with current content.
    /// This cleans up stale entries that may have been left behind.
    /// </summary>
    public int CleanupStalePendingChanges()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            var pendingChanges = _pendingChangesCollection!.FindAll().ToList();
            var removedCount = 0;
            
            foreach (var change in pendingChanges)
            {
                // Check if the file is already backed up
                var backedUp = _filesCollection!.FindOne(x => x.LocalPath == change.FilePath);
                if (backedUp != null && backedUp.Status == BackupStatus.Completed)
                {
                    // Check if the file still exists and matches the backup
                    try
                    {
                        System.IO.FileInfo fileInfo = new(change.FilePath);
                        if (fileInfo.Exists && fileInfo.Length == backedUp.FileSize)
                        {
                            // File is backed up and size matches - remove from pending
                            _pendingChangesCollection.DeleteMany(x => x.FilePath == change.FilePath);
                            removedCount++;
                        }
                    }
                    catch
                    {
                        // Can't access file - leave in pending queue
                    }
                }
                else if (change.ChangeType == FileChangeType.Deleted)
                {
                    // File was deleted and we've recorded it - remove from pending
                    if (backedUp != null && backedUp.Status == BackupStatus.Excluded)
                    {
                        _pendingChangesCollection.DeleteMany(x => x.FilePath == change.FilePath);
                        removedCount++;
                    }
                }
            }
            
            return removedCount;
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    public BackupStatistics GetStatistics()
    {
        EnsureInitialized();

        lock (_dbLock)
        {
            var files = _filesCollection!.FindAll().ToList();
            var config = _configCollection!.FindById(1) ?? new BackupConfiguration();

            return new BackupStatistics
            {
                TotalFiles = files.Count,
                TotalSize = files.Sum(x => x.FileSize),
                CompletedFiles = files.Count(x => x.Status == BackupStatus.Completed),
                PendingFiles = files.Count(x => x.Status == BackupStatus.Pending),
                FailedFiles = files.Count(x => x.Status == BackupStatus.Failed),
                PendingChanges = _pendingChangesCollection!.Count(),
                LastBackupTime = config.LastBackupTime,
                TotalBytesUploaded = config.TotalBytesUploaded,
                EstimatedMonthlyCost = config.EstimatedMonthlyCost
            };
        }
    }

    #endregion

    #region Reset and Secure Delete

    /// <summary>
    /// Securely deletes all data and resets the database to initial state.
    /// Overwrites sensitive data before deletion to prevent recovery.
    /// </summary>
    public void SecureReset()
    {
        lock (_dbLock)
        {
            if (_database == null || string.IsNullOrEmpty(_databasePath))
                return;

            // First, overwrite sensitive data in the database
            OverwriteSensitiveData();
            
            // Close the database
            _database.Dispose();
            _database = null;
            
            // Securely delete the database file
            SecureDeleteFile(_databasePath);
            
            // Also delete the journal file if it exists
            var journalPath = _databasePath + "-journal";
            if (File.Exists(journalPath))
            {
                SecureDeleteFile(journalPath);
            }
            
            // Re-initialize with a fresh database
            Initialize(_databasePath);
        }
    }

    /// <summary>
    /// Overwrites sensitive data in the database before deletion.
    /// </summary>
    private void OverwriteSensitiveData()
    {
        if (_configCollection == null) return;

        var config = _configCollection.FindById(1);
        if (config != null)
        {
            // Overwrite password-related data
            if (config.PasswordSalt != null)
            {
                RandomNumberGenerator.Fill(config.PasswordSalt);
                config.PasswordSalt = null;
            }

            if (config.PasswordVerificationHash != null)
            {
                RandomNumberGenerator.Fill(config.PasswordVerificationHash);
                config.PasswordVerificationHash = null;
            }
            
            // Overwrite encrypted connection string
            if (config.EncryptedConnectionString != null)
            {
                RandomNumberGenerator.Fill(config.EncryptedConnectionString);
                config.EncryptedConnectionString = null;
            }

            // Reset authentication method to default
            config.AuthMethod = AzureAuthMethod.ConnectionString;

            // Reset Entra ID and storage account settings
            config.StorageAccountName = null;
            config.IsEntraIdAuthenticated = false;
            config.EntraIdUserName = null;

            // Reset other sensitive fields
            config.FailedLoginAttempts = 0;
            config.LockoutUntilUtc = null;
            config.WatchedFolders = [];

            _configCollection.Update(config);
        }

        // Clear all file records
        _filesCollection?.DeleteAll();
        _pendingChangesCollection?.DeleteAll();
    }

    /// <summary>
    /// Securely deletes a file by overwriting with random data before deletion.
    /// </summary>
    private static void SecureDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            FileInfo fileInfo = new(filePath);
            var fileSize = fileInfo.Length;

            // Overwrite file with random data (3 passes for extra security)
            using (FileStream stream = new(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[4096];
                
                for (var pass = 0; pass < 3; pass++)
                {
                    stream.Position = 0;
                    var remaining = fileSize;
                    
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, remaining);
                        RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
                        stream.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }
                    
                    stream.Flush();
                }
            }

            // Now delete the file
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // If secure delete fails, try regular delete
            try { File.Delete(filePath); } catch { /* Best effort */ }
        }
    }

    #endregion

    private void EnsureInitialized()
    {
        if (_database == null)
            throw new InvalidOperationException("Database not initialized. Call Initialize first.");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _database?.Dispose();
            _database = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Statistics about the backup state.
/// </summary>
public class BackupStatistics
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public int CompletedFiles { get; set; }
    public int PendingFiles { get; set; }
    public int FailedFiles { get; set; }
    public int PendingChanges { get; set; }
    public DateTime? LastBackupTime { get; set; }
    public long TotalBytesUploaded { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }

    public string TotalSizeFormatted => FormatBytes(TotalSize);
    public string TotalBytesUploadedFormatted => FormatBytes(TotalBytesUploaded);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
