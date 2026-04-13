using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Azure.Core;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Orchestrates the backup process, coordinating between all services.
/// Handles the main backup loop, cost monitoring, and status reporting.
/// Supports both Microsoft Entra ID and Connection String authentication.
/// Uses parallel uploads for optimal bandwidth utilization.
/// </summary>
public partial class BackupOrchestrator : IAsyncDisposable
{
    private readonly LocalDatabaseService _databaseService;
    private readonly EncryptionService _encryptionService;
    private readonly ChunkingService _chunkingService;
    private readonly IBlobStorageService _blobService;
    private readonly FileWatcherService _fileWatcherService;
    private ChunkIndexService? _chunkIndexService;

    private CancellationTokenSource? _backupCts;
    private Task? _backupTask;
    private readonly object _stateLock = new();
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    
    // Entra ID credential (cached after authentication)
    private TokenCredential? _credential;

    // Rate limiting for password attempts
    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutesBase = 15;
    
    // Parallel upload settings - balance between bandwidth and memory usage
    // For a file with many chunks, upload up to 4 chunks simultaneously
    private const int MaxParallelChunkUploads = 4;

    // File-level parallelism for multi-file operations
    // 4 files x 4 chunks = 16 concurrent HTTP uploads max
    private const int MaxParallelFileBackups = 4;

    // Batch size for the background backup monitoring loop
    private const int BackupLoopBatchSize = 50;

    // Memory budget overhead for the CDC buffer (ArrayPool rental in ChunkingService)
    private const long CdcBufferOverhead = 128L * 1024 * 1024;

    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// Subscribe to this event to capture detailed operation logs.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Non-null if the last InitializeAsync succeeded (password valid) but the Azure
    /// connection failed. The UI should show this as a warning, not a login failure.
    /// Cleared on successful connection or when the user reconfigures.
    /// </summary>
    public string? AzureConnectionError { get; private set; }

    /// <summary>
    /// Directory where per-file .diag logs are written on error.
    /// Set by the UI layer (e.g., AppMode.DataDirectory + "diagnostics").
    /// When null, diagnostics are written to the system temp directory.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }

    public BackupOrchestrator(
        LocalDatabaseService databaseService,
        EncryptionService encryptionService,
        ChunkingService chunkingService,
        IBlobStorageService blobService,
        FileWatcherService fileWatcherService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        ArgumentNullException.ThrowIfNull(encryptionService);
        ArgumentNullException.ThrowIfNull(chunkingService);
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(fileWatcherService);

        _databaseService = databaseService;
        _encryptionService = encryptionService;
        _chunkingService = chunkingService;
        _blobService = blobService;
        _fileWatcherService = fileWatcherService;

        _fileWatcherService.FileChanged += OnFileChanged;
        _fileWatcherService.Error += (s, e) => ErrorOccurred?.Invoke(this, e);
        
        Log("BackupOrchestrator initialized");
    }

    /// <summary>
    /// Sets the ChunkIndexService for tracking chunk references.
    /// This enables orphan detection and storage health features.
    /// </summary>
    public void SetChunkIndexService(ChunkIndexService chunkIndexService)
    {
        _chunkIndexService = chunkIndexService;
        Log("ChunkIndexService configured");
    }
    
    /// <summary>
    /// Logs a diagnostic message.
    /// </summary>
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Orchestrator] {message}");
    }
    
    /// <summary>
    /// Gets the WatchedFolder that contains the specified file path.
    /// Returns null if the file is not in any watched folder.
    /// </summary>
    private WatchedFolder? GetWatchedFolderForFile(string filePath)
    {
        var config = _databaseService.GetConfiguration();
        
        // Find the watched folder that contains this file
        // Use case-insensitive comparison for Windows paths
        return config.WatchedFolders
            .Where(f => f.IsEnabled)
            .FirstOrDefault(f => filePath.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Gets the storage tier for a file based on its WatchedFolder configuration.
    /// Defaults to Hot if file is not in a watched folder.
    /// </summary>
    private StorageTier GetStorageTierForFile(string filePath)
    {
        var folder = GetWatchedFolderForFile(filePath);
        return folder?.StorageTier ?? StorageTier.Hot;
    }

    /// <summary>
    /// Initializes the orchestrator with a password.
    /// Includes rate limiting to prevent brute force attacks.
    /// </summary>
    public async Task<bool> InitializeAsync(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Log("InitializeAsync: Starting initialization");
        
        var config = _databaseService.GetConfiguration();
        Log($"InitializeAsync: Config loaded, AuthMethod={config.AuthMethod}, HasSalt={config.PasswordSalt != null}");

        // Check for lockout
        if (config.LockoutUntilUtc.HasValue && DateTime.UtcNow < config.LockoutUntilUtc.Value)
        {
            var remaining = config.LockoutUntilUtc.Value - DateTime.UtcNow;
            throw new SecurityPolicyException(
                $"Account locked due to too many failed attempts. Try again in {remaining.TotalMinutes:F0} minutes.",
                SecurityPolicyType.AccountLocked);
        }

        if (config.PasswordSalt == null)
        {
            // First time setup - enforce password strength
            Log("InitializeAsync: First time setup - validating password strength");
            PasswordValidator.Validate(password);
            
            // First time setup - create new salt
            config.PasswordSalt = EncryptionService.GenerateSalt();
            config.PasswordVerificationHash = await _encryptionService.CreatePasswordVerificationHashAsync(
                password, config.PasswordSalt);
            config.FailedLoginAttempts = 0;
            config.LockoutUntilUtc = null;
            _databaseService.SaveConfiguration(config);
            Log("InitializeAsync: New password configured and saved");
        }
        else
        {
            // Verify password
            Log("InitializeAsync: Verifying existing password");
            var isValid = await _encryptionService.VerifyPasswordAsync(
                password, config.PasswordSalt, config.PasswordVerificationHash!);
            
            if (!isValid)
            {
                // Record failed attempt
                config.FailedLoginAttempts++;
                Log($"InitializeAsync: Password verification failed, attempt #{config.FailedLoginAttempts}");
                
                if (config.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    // Calculate exponential backoff lockout
                    var lockoutMinutes = LockoutMinutesBase * Math.Pow(2, config.FailedLoginAttempts - MaxFailedAttempts);
                    config.LockoutUntilUtc = DateTime.UtcNow.AddMinutes(lockoutMinutes);
                    Log($"InitializeAsync: Account locked for {lockoutMinutes} minutes");
                }
                
                _databaseService.SaveConfiguration(config);
                return false;
            }
            
            // Successful login - reset failed attempts
            Log("InitializeAsync: Password verified successfully");
            if (config.FailedLoginAttempts > 0)
            {
                config.FailedLoginAttempts = 0;
                config.LockoutUntilUtc = null;
                _databaseService.SaveConfiguration(config);
                Log("InitializeAsync: Reset failed login counter");
            }
        }

        // Derive and set encryption key, then zero the key array
        Log("InitializeAsync: Deriving encryption key");
        var key = await _encryptionService.DeriveKeyAsync(password, config.PasswordSalt);
        try
        {
            _encryptionService.Initialize(key);
            Log("InitializeAsync: Encryption service initialized");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }

        // Connect to Azure based on configured auth method
        // Failures here are non-fatal — the password was valid, encryption is ready,
        // and the user can fix connection issues from Settings without re-entering the password.
        Log("InitializeAsync: Connecting to Azure storage");
        try
        {
            await ConnectToAzureAsync(config);
            Log("InitializeAsync: Initialization complete");
        }
        catch (Exception ex)
        {
            Log($"InitializeAsync: Azure connection failed (non-fatal): {ex.Message}");
            AzureConnectionError = ex.Message;
            ErrorOccurred?.Invoke(this,
                $"Password accepted but Azure connection failed: {ex.Message}. " +
                "You can fix connection settings and retry from Settings.");
        }

        return true;
    }
    
    /// <summary>
    /// Connects to Azure based on the configured authentication method.
    /// </summary>
    private async Task ConnectToAzureAsync(BackupConfiguration config)
    {
        AzureConnectionError = null;
        var containerName = config.ContainerName ?? "backup";
        Log($"ConnectToAzureAsync: AuthMethod={config.AuthMethod}, Container={containerName}");
        
        if (config.AuthMethod == AzureAuthMethod.EntraId)
        {
            // Entra ID authentication
            if (config.IsEntraIdAuthenticated && config.BlobServiceUri != null && _credential != null)
            {
                Log($"ConnectToAzureAsync: Connecting with Entra ID to {config.BlobServiceUri}");
                await _blobService.ConnectWithEntraIdAsync(
                    config.BlobServiceUri, 
                    containerName, 
                    _credential);
                Log("ConnectToAzureAsync: Entra ID connection established");
            }
            else
            {
                Log("ConnectToAzureAsync: Skipping Entra ID connection - not fully configured");
            }
        }
        else
        {
            // Connection String authentication
            if (config.EncryptedConnectionString != null && _encryptionService.IsInitialized)
            {
                Log("ConnectToAzureAsync: Decrypting and connecting with connection string");
                var decrypted = _encryptionService.Decrypt(config.EncryptedConnectionString);
                try
                {
                    var connectionString = System.Text.Encoding.UTF8.GetString(decrypted);
                    await _blobService.ConnectAsync(connectionString, containerName);
                    Log("ConnectToAzureAsync: Connection string connection established");
                }
                finally
                {
                    // Clear decrypted connection string from memory
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(decrypted);
                }
            }
            else
            {
                Log("ConnectToAzureAsync: Skipping connection string connection - not configured");
            }
        }
    }

    /// <summary>
    /// Securely resets all application data and settings.
    /// This will clear the encryption key, credentials, and file records.
    /// The user will need to set up a new password and re-authenticate.
    /// </summary>
    public async Task ResetApplicationAsync()
    {
        // Stop any running backup operations first
        if (_isRunning)
        {
            await StopAsync();
        }

        // Clear the encryption key from memory
        _encryptionService.ClearKey();
        
        // Clear Entra ID credential
        _credential = null;

        // Securely delete all database data
        _databaseService.SecureReset();

        StatusChanged?.Invoke(this, "Application reset complete. Please set up a new password and configure Azure connection.");
    }

    /// <summary>
    /// Starts the backup service.
    /// </summary>
    public void Start()
    {
        lock (_stateLock)
        {
            if (_isRunning) return;

            _backupCts = new CancellationTokenSource();
            _fileWatcherService.Start();
            _backupTask = RunBackupLoopAsync(_backupCts.Token);
            _isRunning = true;
            _isPaused = false;
        }

        StatusChanged?.Invoke(this, "Backup service started");
    }

    /// <summary>
    /// Stops the backup service.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        
        lock (_stateLock)
        {
            if (!_isRunning) return;
            
            cts = _backupCts;
            task = _backupTask;
            _backupCts = null;
            _backupTask = null;
        }

        cts?.Cancel();
        _fileWatcherService.Stop();

        if (task != null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        
        cts?.Dispose();

        lock (_stateLock)
        {
            _isRunning = false;
            _isPaused = false;
        }
        
        StatusChanged?.Invoke(this, "Backup service stopped");
    }

    /// <summary>
    /// Backs up a single file.
    /// </summary>
    public Task<bool> BackupFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return BackupFileAsync(filePath, progress: null, memoryBudget: null, cancellationToken);
    }

    /// <summary>
    /// Backs up a single file with progress reporting.
    /// </summary>
    /// <param name="filePath">Path to the file to backup</param>
    /// <param name="progress">Reports byte-level progress (bytes completed, total bytes)</param>
    /// <param name="memoryBudget">Optional memory budget for throttling parallel chunk uploads.
    /// When null, upload parallelism is unbounded (existing behavior).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> BackupFileAsync(
        string filePath, 
        IProgress<(long current, long total)>? progress,
        MemoryBudget? memoryBudget = null,
        CancellationToken cancellationToken = default)
    {
        var diag = new FileOperationDiagnostics(filePath, "Backup", DiagnosticsDirectory);
        using var _ = diag.SetAmbient();
        try
        {
            if (!File.Exists(filePath))
            {
                // File was deleted - update record
                var existingRecord = _databaseService.GetBackedUpFile(filePath);
                if (existingRecord != null)
                {
                    existingRecord.Status = BackupStatus.Excluded;
                    _databaseService.SaveBackedUpFile(existingRecord);
                }
                return true;
            }

            // Wait for file to be available
            if (FileWatcherService.IsFileLocked(filePath))
            {
                StatusChanged?.Invoke(this, $"Waiting for file: {Path.GetFileName(filePath)}");
                var available = await FileWatcherService.WaitForFileAsync(filePath, TimeSpan.FromMinutes(5), cancellationToken);
                if (!available)
                {
                    ErrorOccurred?.Invoke(this, $"File locked, skipping: {filePath}");
                    return false;
                }
            }

            // Get file info and existing backup record
            FileInfo fileInfo = new(filePath);
            var existingFile = _databaseService.GetBackedUpFile(filePath);

            // Quick metadata check: skip expensive file reads if size and timestamp match.
            // For unchanged files this avoids reading the entire file just to hash it.
            if (existingFile != null && existingFile.Status == BackupStatus.Completed &&
                existingFile.FileSize == fileInfo.Length &&
                Math.Abs((fileInfo.LastWriteTimeUtc - existingFile.LastModified).TotalSeconds) < 2)
            {
                StatusChanged?.Invoke(this, $"Unchanged, skipping: {Path.GetFileName(filePath)}");
                Log($"BackupFileAsync: Metadata unchanged, skipping: {Path.GetFileName(filePath)}");
                progress?.Report((fileInfo.Length, fileInfo.Length));
                return true;
            }

            var fileName = Path.GetFileName(filePath);
            StatusChanged?.Invoke(this, $"Analyzing: {fileName} ({FormatHelper.FormatBytes(fileInfo.Length)})");

            diag.Record($"File: size={fileInfo.Length:N0}, lastWrite={fileInfo.LastWriteTimeUtc:O}, isNew={existingFile == null}");
            if (existingFile != null)
            {
                diag.Record($"Existing backup: hash={existingFile.FileHash?[..8]}..., chunks={existingFile.Chunks?.Count}, status={existingFile.Status}");
            }
            // Determine which chunks need uploading using existing backup's chunk hashes
            var existingChunks = existingFile?.Chunks ?? [];
            var existingHashes = existingChunks.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);

            // For new files (no existing backup), skip existence checks - all chunks are new
            var isNewFile = existingFile == null;

            // Get the storage tier based on the watched folder configuration
            var storageTier = GetStorageTierForFile(filePath);

            // Pipeline: CDC + filtered upload in a single file open.
            // The bounded channel provides backpressure — the producer blocks when
            // MaxParallelChunkUploads consumers are busy uploading.
            var channel = Channel.CreateBounded<ChunkPayload>(new BoundedChannelOptions(MaxParallelChunkUploads)
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            long bytesUploaded = 0;
            var totalFileSize = fileInfo.Length;

            // CDC progress: report chunking phase to the user so large files don't appear hung
            Progress<(long bytesProcessed, long totalBytes, int chunksFound)> cdcProgress = new(p =>
            {
                StatusChanged?.Invoke(this, $"Chunking: {fileName} — {FormatHelper.FormatBytes(p.bytesProcessed)}/{FormatHelper.FormatBytes(p.totalBytes)} ({p.chunksFound} chunks)");
            });

            // Producer: CDC pass + filtered seek pass, writes changed chunks to channel
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    var (producedChunks, producedHash) = await _chunkingService.ChunkAndStreamChangedAsync(
                        filePath, existingHashes, channel.Writer, cdcProgress, cancellationToken);
                    return (producedChunks, producedHash);
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            // Track chunks uploaded for status messages
            int chunksUploadedCount = 0;

            // Consumers: upload workers read from channel in parallel
            var consumerTasks = Enumerable.Range(0, MaxParallelChunkUploads).Select(async _ =>
            {
                await foreach (var payload in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    // Memory budget: acquire the cost of plaintext + encrypted copy.
                    // EncryptInto writes into a rented buffer inside the blob service,
                    // so the actual in-flight memory per chunk is ~2× plaintext size.
                    var chunkMemoryCost = (long)payload.Length * 2;
                    if (memoryBudget != null)
                        await memoryBudget.AcquireAsync(chunkMemoryCost, cancellationToken);
                    try
                    {
                        // Slice the rented buffer to actual data extent for upload
                        var chunkData = payload.Data.AsMemory(0, payload.Length);

                        // Snapshot first+last bytes of plaintext BEFORE upload to detect
                        // race conditions where ZeroMemory in another consumer corrupts
                        // the buffer before Encrypt finishes reading it.
                        var preFirstByte = payload.Length > 0 ? payload.Data[0] : (byte)0;
                        var preLastByte = payload.Length > 1 ? payload.Data[payload.Length - 1] : (byte)0;

                        diag.RecordChunk("PreUpload", payload.Info.Index, payload.Info.Hash,
                            payload.Length,
                            extra: $"isNew={isNewFile}, tier={storageTier}, first=0x{preFirstByte:X2}, last=0x{preLastByte:X2}");

                        var uploadProgress = new Progress<long>(b =>
                        {
                            var uploaded = Interlocked.Add(ref bytesUploaded, b);
                            progress?.Report((uploaded, totalFileSize));
                        });

                        payload.Info.BlobName = isNewFile
                            ? await _blobService.UploadChunkDirectAsync(chunkData, payload.Info.Hash, storageTier,
                                uploadProgress, cancellationToken)
                            : await _blobService.UploadChunkAsync(chunkData, payload.Info.Hash, storageTier,
                                uploadProgress, cancellationToken);

                        // Check if plaintext was zeroed during upload (race condition indicator).
                        // Encrypt copies into its own buffer so this SHOULD still be non-zero
                        // unless another consumer's ZeroMemory hit this buffer.
                        var postFirstByte = payload.Length > 0 ? payload.Data[0] : (byte)0;
                        var postLastByte = payload.Length > 1 ? payload.Data[payload.Length - 1] : (byte)0;
                        var bufferIntact = preFirstByte == postFirstByte && preLastByte == postLastByte;

                        diag.RecordChunk("Uploaded", payload.Info.Index, payload.Info.Hash,
                            payload.Length,
                            extra: $"blob={payload.Info.BlobName}, bufferIntact={bufferIntact}, " +
                                   $"post_first=0x{postFirstByte:X2}, post_last=0x{postLastByte:X2}");

                        if (!bufferIntact)
                        {
                            diag.Record($"[RACE?] Chunk {payload.Info.Index} plaintext buffer MODIFIED during upload! " +
                                $"pre_first=0x{preFirstByte:X2}→0x{postFirstByte:X2}, " +
                                $"pre_last=0x{preLastByte:X2}→0x{postLastByte:X2}");
                        }

                        var completed = Interlocked.Increment(ref chunksUploadedCount);
                        StatusChanged?.Invoke(this, $"Uploading: {fileName} — chunk {completed} ({FormatHelper.FormatBytes(Interlocked.Read(ref bytesUploaded))}/{FormatHelper.FormatBytes(totalFileSize)})");
                    }
                    finally
                    {
                        diag.RecordChunk("ZeroMemory", payload.Info.Index, payload.Info.Hash,
                            payload.Length,
                            extra: $"firstByte=0x{(payload.Length > 0 ? payload.Data[0] : 0):X2}");
                        CryptographicOperations.ZeroMemory(payload.Data.AsSpan(0, payload.Length));
                        ArrayPool<byte>.Shared.Return(payload.Data);
                        memoryBudget?.Release(chunkMemoryCost);
                    }
                }
            }).ToArray();

            // Wait for both producer and all consumers to complete
            var (chunks, fileHash) = await producerTask;
            await Task.WhenAll(consumerTasks);

            diag.Record($"CDC complete: {chunks.Count} chunks, fileHash={fileHash[..12]}..., totalChunkBytes={chunks.Sum(c => (long)c.Length):N0}");

            // Hash-level verification: if file content is actually identical despite metadata difference
            if (existingFile != null && existingFile.FileHash == fileHash)
            {
                StatusChanged?.Invoke(this, $"Verified unchanged: {fileName}");
                Log($"BackupFileAsync: Hash unchanged despite metadata change, skipping: {fileName}");
                progress?.Report((fileInfo.Length, fileInfo.Length));
                return true;
            }

            var chunksToUpload = chunks.Where(c => !existingHashes.Contains(c.Hash)).ToList();
            Log($"BackupFileAsync: '{fileName}' is {(isNewFile ? "NEW" : "EXISTING")}, {chunksToUpload.Count} chunks uploaded, tier={storageTier}");

            // Save file metadata
            StatusChanged?.Invoke(this, $"Saving metadata: {fileName}");
            var metadataHash = _encryptionService.ComputeHmacHex(filePath);
            BackedUpFile backedUpFile = new()
            {
                LocalPath = filePath,
                BlobName = $"metadata/{metadataHash}",
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                FileHash = fileHash,
                Chunks = chunks,
                BackedUpAt = DateTime.UtcNow,
                Status = BackupStatus.Completed
            };

            await _blobService.UploadFileMetadataAsync(backedUpFile, storageTier, cancellationToken);
            _databaseService.SaveBackedUpFile(backedUpFile);

            // Track chunk references in the chunk index
            if (_chunkIndexService != null)
            {
                // Get old chunk hashes if this is a modified file
                var oldChunkHashes = existingFile?.Chunks?.Select(c => c.Hash).ToList() ?? [];
                
                if (isNewFile || oldChunkHashes.Count == 0)
                {
                    // New file - add references for all chunks
                    foreach (var chunk in chunks)
                    {
                        var wasNewUpload = chunksToUpload.Contains(chunk);
                        _chunkIndexService.AddReference(
                            chunk.Hash, 
                            filePath, 
                            chunk.Index, 
                            chunk.Length, 
                            storageTier, 
                            wasNewUpload);
                    }
                    Log($"BackupFileAsync: Added {chunks.Count} chunk references for new file '{fileName}'");
                }
                else
                {
                    // Modified file - update chunk references
                    var newChunkInfo = chunks.Select(c => (
                        hash: c.Hash, 
                        index: c.Index, 
                        size: (long)c.Length, 
                        isNew: chunksToUpload.Contains(c)
                    )).ToList();
                    
                    await _chunkIndexService.UpdateFileChunksAsync(
                        filePath, 
                        oldChunkHashes, 
                        newChunkInfo, 
                        storageTier, 
                        cancellationToken);
                    Log($"BackupFileAsync: Updated chunk references for modified file '{fileName}'");
                }
                
                // Verify consistency after backup
                _chunkIndexService.VerifyBackupConsistency(filePath, chunks.Select(c => c.Hash).ToList());
            }

            // Update config stats
            var config = _databaseService.GetConfiguration();
            config.TotalBytesUploaded += bytesUploaded;
            config.LastBackupTime = DateTime.UtcNow;
            _databaseService.SaveConfiguration(config);

            // Report final progress
            progress?.Report((totalFileSize, totalFileSize));

            ProgressChanged?.Invoke(this, new BackupProgressEventArgs
            {
                FilePath = filePath,
                BytesUploaded = bytesUploaded,
                ChunksUploaded = chunksToUpload.Count,
                TotalChunks = chunks.Count
            });

            StatusChanged?.Invoke(this, $"Completed: {fileName} — {chunksToUpload.Count}/{chunks.Count} chunks uploaded ({FormatHelper.FormatBytes(bytesUploaded)})");

            return true;
        }
        catch (Exception ex)
        {
            diag.RecordError("BackupFileAsync", ex);
            var diagPath = diag.Flush($"Backup failed: {ex.Message}");
            if (diagPath != null)
            {
                Log($"BackupFileAsync: Diagnostics written to {diagPath}");
            }

            ErrorOccurred?.Invoke(this, $"Failed to backup {filePath}: {ex.Message}");

            var failedFile = _databaseService.GetBackedUpFile(filePath) ?? new BackedUpFile { LocalPath = filePath };
            failedFile.Status = BackupStatus.Failed;
            _databaseService.SaveBackedUpFile(failedFile);

            return false;
        }
    }

    private async Task RunBackupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_isPaused)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                // Process pending changes in larger batches
                var pendingChanges = _databaseService.GetPendingChanges(BackupLoopBatchSize);

                if (pendingChanges.Count == 0)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                // Separate deletions (sequential, involves index operations) from backups (parallelizable)
                var deletions = pendingChanges.Where(c => c.ChangeType == FileChangeType.Deleted).ToList();
                var backups = pendingChanges.Where(c => c.ChangeType != FileChangeType.Deleted).ToList();

                // Handle deletions sequentially - they involve chunk index operations
                foreach (var change in deletions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var existingFile = _databaseService.GetBackedUpFile(change.FilePath);
                    if (existingFile != null)
                    {
                        if (_chunkIndexService != null)
                        {
                            var deletedChunks = await _chunkIndexService.RemoveFileReferencesAsync(
                                change.FilePath, cancellationToken);
                            if (deletedChunks > 0)
                            {
                                Log($"RunBackupLoopAsync: Deleted {deletedChunks} orphaned chunks " +
                                    $"after file deletion: {change.FilePath}");
                            }
                        }

                        existingFile.Status = BackupStatus.Excluded;
                        _databaseService.SaveBackedUpFile(existingFile);
                    }

                    _databaseService.RemovePendingChange(change.FilePath);
                }

                // Process backup files in parallel
                if (backups.Count > 0)
                {
                    // Create a shared memory budget for this batch.
                    // Re-read config each iteration so slider changes take effect immediately.
                    var batchConfig = _databaseService.GetConfiguration();
                    using var batchBudget = MemoryBudget.FromConfig(batchConfig, CdcBufferOverhead);

                    Log($"RunBackupLoopAsync: Processing {backups.Count} files in parallel " +
                        $"(max {MaxParallelFileBackups}, " +
                        $"memoryBudget={(!batchBudget.IsUnlimited ? $"{batchConfig.MemoryLimitMB} MB" : "unlimited")})");

                    await Parallel.ForEachAsync(
                        backups,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = MaxParallelFileBackups,
                            CancellationToken = cancellationToken
                        },
                        async (change, ct) =>
                        {
                            await BackupFileAsync(change.FilePath, progress: null, batchBudget, ct);
                            _databaseService.RemovePendingChange(change.FilePath);
                        });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Backup loop error: {ex.Message}");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private void OnFileChanged(object? sender, FileChangeEvent e)
    {
        StatusChanged?.Invoke(this, $"Detected change: {Path.GetFileName(e.FilePath)}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _backupCts?.Dispose();
    }
}
