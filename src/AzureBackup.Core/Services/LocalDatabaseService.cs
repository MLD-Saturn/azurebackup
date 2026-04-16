using System.Diagnostics;
using System.Security.Cryptography;
using AzureBackup.Core.Models;
using Konscious.Security.Cryptography;
using LiteDB;

namespace AzureBackup.Core.Services;

/// <summary>
/// Manages local database for tracking backup state, configuration, and file metadata.
/// Uses LiteDB for embedded, portable storage (no installation required).
/// The database is encrypted using a key derived from the user's password via Argon2id,
/// providing strong protection against brute force attacks.
/// </summary>
public partial class LocalDatabaseService : IDisposable
{
    private LiteDatabase? _database;
    private ILiteCollection<BackupConfiguration>? _configCollection;
    private ILiteCollection<BackedUpFile>? _filesCollection;
    private ILiteCollection<FileChangeEvent>? _pendingChangesCollection;
    private ILiteCollection<ChunkIndexEntry>? _chunkIndexCollection;
    private ILiteCollection<IndexMetadata>? _indexMetadataCollection;
    private readonly object _dbLock = new();
    private bool _disposed;
    private string? _databasePath;

    // Argon2id parameters - matches EncryptionService for consistency
    private const int Argon2DegreeOfParallelism = 8;
    private const int Argon2MemorySize = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int SaltSize = 16;
    private const int DerivedKeySize = 32; // 256 bits

    public bool IsInitialized => _database != null;
    
    /// <summary>
    /// Gets the current database file path.
    /// </summary>
    public string? DatabasePath => _databasePath;
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Database] {message}");
    }

    /// <summary>
    /// Gets the path to the database salt file.
    /// </summary>
    private static string GetSaltFilePath(string databasePath) => databasePath + ".salt";

    /// <summary>
    /// Initializes the database at the specified path with password encryption.
    /// Uses Argon2id to derive a strong key from the password, providing protection
    /// against brute force attacks even if the database file is stolen.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <param name="password">Password used to encrypt the database. Supplied as a span so the
    /// caller can keep the plaintext in a <c>char[]</c> and zero it after use.</param>
    /// <exception cref="InvalidPasswordException">Thrown if password is incorrect for existing database</exception>
    public void Initialize(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        Log($"Initialize: Opening encrypted database at {databasePath}");

        _databasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            Log($"Initialize: Created directory {directory}");
        }

        // Get or create the database salt
        var saltFilePath = GetSaltFilePath(databasePath);
        byte[] salt;

        if (File.Exists(saltFilePath))
        {
            // Existing database - read salt
            salt = File.ReadAllBytes(saltFilePath);
            if (salt.Length != SaltSize)
            {
                throw new InvalidOperationException($"Database salt file is corrupted (expected {SaltSize} bytes, got {salt.Length})");
            }
            Log("Initialize: Loaded existing database salt");
        }
        else
        {
            // New database - generate and save salt
            salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            File.WriteAllBytes(saltFilePath, salt);
            Log("Initialize: Generated and saved new database salt");
        }

        // Derive strong key using Argon2id (same parameters as EncryptionService)
        Log("Initialize: Deriving database key with Argon2id...");
        var derivedKey = DeriveKeyFromPassword(password, salt);

        try
        {
            // Convert derived key to Base64 for LiteDB password
            // LiteDB will use this as the encryption password
            var dbPassword = Convert.ToBase64String(derivedKey);

            // Build connection string with derived key
            var connectionString = new ConnectionString
            {
                Filename = databasePath,
                Password = dbPassword,
                Connection = ConnectionType.Shared
            };

            try
            {
                _database = new LiteDatabase(connectionString);

                _configCollection = _database.GetCollection<BackupConfiguration>("config");
                _filesCollection = _database.GetCollection<BackedUpFile>("files");
                _pendingChangesCollection = _database.GetCollection<FileChangeEvent>("pending_changes");
                _chunkIndexCollection = _database.GetCollection<ChunkIndexEntry>("chunk_index");
                _indexMetadataCollection = _database.GetCollection<IndexMetadata>("index_metadata");

                // Create indexes for faster queries
                _filesCollection.EnsureIndex(x => x.LocalPath, unique: true);
                _filesCollection.EnsureIndex(x => x.Status);
                _filesCollection.EnsureIndex(x => x.FileHash);

                // Chunk index indexes
                _chunkIndexCollection.EnsureIndex(x => x.ChunkHash, unique: true);
                _chunkIndexCollection.EnsureIndex(x => x.ReferenceCount);
                _chunkIndexCollection.EnsureIndex(x => x.CurrentTier);
            }
            catch (LiteException ex) when (ex.Message.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("file is not a valid", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("HMAC", StringComparison.OrdinalIgnoreCase))
            {
                Log("Initialize: Invalid password for encrypted database");
                _database?.Dispose();
                _database = null;
                throw new InvalidPasswordException("Invalid password. Please try again.", ex);
            }
        }
        finally
        {
            // Zero the derived key from memory
            CryptographicOperations.ZeroMemory(derivedKey);
        }

        Log("Initialize: Encrypted database initialized successfully with Argon2id-derived key");
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="Initialize(string, ReadOnlySpan{char})"/>.
    /// Prefer the span overload so the plaintext password does not linger on the managed heap.
    /// </summary>
    public void Initialize(string databasePath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Initialize(databasePath, password.AsSpan());
    }

    /// <summary>
    /// Derives a key from a password using Argon2id.
    /// Uses the same parameters as EncryptionService for consistency.
    /// </summary>
    private static byte[] DeriveKeyFromPassword(ReadOnlySpan<char> password, byte[] salt)
    {
        var passwordBytes = PasswordBytes.FromChars(password);
        try
        {
            using Argon2id argon2 = new(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations
            };

            return argon2.GetBytes(DerivedKeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Closes the current database connection.
    /// Used when migrating to allow reopening with different settings.
    /// </summary>
    public void Close()
    {
        lock (_dbLock)
        {
            _database?.Dispose();
            _database = null;
            _configCollection = null;
            _filesCollection = null;
            _pendingChangesCollection = null;
            _chunkIndexCollection = null;
            _indexMetadataCollection = null;
            Log("Close: Database connection closed");
        }
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
    /// Bulk variant of <see cref="QueueFileChange"/>. All inserts run inside a single
    /// LiteDB transaction, avoiding the per-change acquire/release of <c>_dbLock</c>
    /// and the per-change journal write. Preserves the "replace existing" semantics
    /// of the single-change variant by collecting affected paths first and issuing
    /// one <c>DeleteMany</c> before the bulk insert.
    /// </summary>
    /// <remarks>
    /// At ~10k changes (e.g. IDE rebuild or git checkout) this turns ~10k small
    /// transactions into a single one, cutting total commit time by 1-2 orders of
    /// magnitude and eliminating contention with the backup loop that reads from
    /// the same collection.
    /// </remarks>
    /// <param name="changes">
    /// The changes to persist. An empty or null sequence is a no-op. If multiple
    /// entries in the sequence share a <see cref="FileChangeEvent.FilePath"/> the
    /// last one wins - matching the semantics of repeated single-change calls.
    /// </param>
    public void QueueFileChangesBatch(IEnumerable<FileChangeEvent> changes)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(changes);

        // Materialise once and deduplicate by FilePath (last-write-wins) so we can
        // issue a single DeleteMany covering every affected path before the insert.
        var byPath = new Dictionary<string, FileChangeEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change is null) continue;
            byPath[change.FilePath] = change;
        }

        if (byPath.Count == 0) return;

        lock (_dbLock)
        {
            _database!.BeginTrans();
            try
            {
                // Remove existing pending rows for every affected path, then bulk-insert.
                // The DeleteMany predicate on LiteDB compiles to a BSON query, so we pass
                // a simple equality check per path rather than relying on HashSet.Contains
                // which LiteDB's expression visitor does not support.
                foreach (var path in byPath.Keys)
                {
                    _pendingChangesCollection!.DeleteMany(x => x.FilePath == path);
                }

                // InsertBulk is the LiteDB batch-insert primitive.
                _pendingChangesCollection!.InsertBulk(byPath.Values);
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
    /// Gets all pending change file paths as a set for fast lookups.
    /// Use this instead of per-file IsFileChangePending calls when checking many files.
    /// </summary>
    public HashSet<string> GetAllPendingChangePaths()
    {
        EnsureInitialized();

        lock (_dbLock)
        {
            return _pendingChangesCollection!
                .Query()
                .Select(x => x.FilePath)
                .ToEnumerable()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
