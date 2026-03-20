using System.Diagnostics;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Orchestrates the backup process, coordinating between all services.
/// Handles the main backup loop, cost monitoring, and status reporting.
/// Supports both Microsoft Entra ID and Connection String authentication.
/// Uses parallel uploads for optimal bandwidth utilization.
/// </summary>
public class BackupOrchestrator : IAsyncDisposable
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

    #region Entra ID Authentication
    
    /// <summary>
    /// Authenticates with Microsoft Entra ID using interactive browser flow.
    /// Opens the system's default browser for seamless sign-in.
    /// Use this for organizational/work accounts only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <param name="timeoutSeconds">Timeout in seconds for the authentication (default: 120 seconds)</param>
    public async Task<(bool success, string message)> AuthenticateWithEntraIdAsync(
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 120)
    {
        Log($"AuthenticateWithEntraIdAsync: Starting browser authentication (timeout={timeoutSeconds}s)");
        try
        {
            InteractiveBrowserCredentialOptions options = new()
            {
                // Use the system's default browser
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "AzureBackup",
                    UnsafeAllowUnencryptedStorage = false
                },
                // Redirect to localhost after auth completes
                RedirectUri = new Uri("http://localhost"),
                // Set a reasonable timeout for browser interaction
                BrowserCustomization = new BrowserCustomizationOptions
                {
                    UseEmbeddedWebView = false
                }
            };
            
            _credential = new InteractiveBrowserCredential(options);
            Log("AuthenticateWithEntraIdAsync: Opening browser for authentication");
            
            // Create a timeout cancellation token
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            // Force a token request to trigger the browser login
            TokenRequestContext tokenRequest = new(
                ["https://storage.azure.com/.default"]);
            
            var token = await _credential.GetTokenAsync(tokenRequest, linkedCts.Token);
            
            if (!string.IsNullOrEmpty(token.Token))
            {
                Log("AuthenticateWithEntraIdAsync: Token obtained successfully");
                // Update config to mark as authenticated
                var config = _databaseService.GetConfiguration();
                config.IsEntraIdAuthenticated = true;
                _databaseService.SaveConfiguration(config);
                
                return (true, "Successfully authenticated with Microsoft Entra ID!");
            }
            
            Log("AuthenticateWithEntraIdAsync: No token returned");
            _credential = null;
            return (false, "Authentication did not return a valid token.");
        }
        catch (AuthenticationFailedException ex)
        {
            Log($"AuthenticateWithEntraIdAsync: AuthenticationFailedException - {ex.Message}");
            _credential = null;
            // Provide more user-friendly messages for common errors
            if (ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Sign-in was cancelled. Please try again.");
            }
            if (ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Microsoft sign-in error: {ex.Message}");
            }
            return (false, $"Authentication failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("AuthenticateWithEntraIdAsync: Cancelled by user");
            _credential = null;
            return (false, "Sign-in was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            Log($"AuthenticateWithEntraIdAsync: Timeout after {timeoutSeconds} seconds");
            _credential = null;
            return (false, $"Sign-in timed out after {timeoutSeconds} seconds. Please try again.");
        }
        catch (Exception ex)
        {
            Log($"AuthenticateWithEntraIdAsync: Exception - {ex.GetType().Name}: {ex.Message}");
            _credential = null;
            // Handle browser-related errors
            if (ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Could not open browser for sign-in. Please ensure a browser is available.");
            }
            return (false, $"Authentication error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Tests the connection to Azure storage using the current Entra ID credential.
    /// </summary>
    public async Task<(bool success, string message)> TestAzureConnectionAsync(
        string storageAccountName, string containerName)
    {
        Log($"TestAzureConnectionAsync: Testing Entra ID connection to {storageAccountName}/{containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (_credential == null)
        {
            Log("TestAzureConnectionAsync: No credential available");
            return (false, "Not authenticated with Entra ID. Please sign in first.");
        }
        
        Uri blobServiceUri = new($"https://{storageAccountName}.blob.core.windows.net");
        var result = await _blobService.TestConnectionWithEntraIdAsync(blobServiceUri, containerName, _credential);
        Log($"TestAzureConnectionAsync: Result success={result.success}");
        return result;
    }
    
    /// <summary>
    /// Saves the Azure storage account configuration (uses Entra ID, no connection string needed).
    /// </summary>
    public async Task SaveStorageAccountAsync(string storageAccountName, string containerName)
    {
        Log($"SaveStorageAccountAsync: Saving Entra ID config for {storageAccountName}/{containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (_credential == null)
            throw new InvalidOperationException("Must authenticate with Entra ID first.");
        
        var config = _databaseService.GetConfiguration();
        config.StorageAccountName = storageAccountName;
        config.ContainerName = containerName;
        config.IsEntraIdAuthenticated = true;
        config.AuthMethod = AzureAuthMethod.EntraId;
        _databaseService.SaveConfiguration(config);
        Log("SaveStorageAccountAsync: Configuration saved");
        
        // Connect immediately
        await _blobService.ConnectWithEntraIdAsync(
            config.BlobServiceUri!, 
            containerName, 
            _credential);
        Log("SaveStorageAccountAsync: Connected to Azure storage");
    }
    
    /// <summary>
    /// Gets whether the user is currently authenticated with Entra ID.
    /// </summary>
    public bool IsEntraIdAuthenticated => _credential != null;

    #endregion

    #region Connection String Authentication

    /// <summary>
    /// Tests the connection using a connection string.
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionStringAsync(
        string connectionString, string containerName)
    {
        Log($"TestConnectionStringAsync: Testing connection string to container {containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        var result = await _blobService.TestConnectionAsync(connectionString, containerName);
        Log($"TestConnectionStringAsync: Result success={result.success}");
        return result;
    }

    /// <summary>
    /// Saves and connects using a connection string (encrypts it before storing).
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task SaveConnectionStringAsync(string connectionString, string containerName)
    {
        Log($"SaveConnectionStringAsync: Saving connection string config for container {containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (!_encryptionService.IsInitialized)
            throw new InvalidOperationException("Encryption service must be initialized before saving connection string.");
        
        var config = _databaseService.GetConfiguration();
        config.EncryptedConnectionString = _encryptionService.Encrypt(
            System.Text.Encoding.UTF8.GetBytes(connectionString));
        config.ContainerName = containerName;
        config.AuthMethod = AzureAuthMethod.ConnectionString;
        config.IsEntraIdAuthenticated = false;
        _databaseService.SaveConfiguration(config);
        Log("SaveConnectionStringAsync: Encrypted connection string saved");
        
        // Connect immediately
        await _blobService.ConnectAsync(connectionString, containerName);
        Log("SaveConnectionStringAsync: Connected to Azure storage");
    }

    #endregion

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
    /// Pauses the backup service.
    /// </summary>
    public void Pause()
    {
        _isPaused = true;
        StatusChanged?.Invoke(this, "Backup service paused");
    }

    /// <summary>
    /// Resumes the backup service.
    /// </summary>
    public void Resume()
    {
        _isPaused = false;
        StatusChanged?.Invoke(this, "Backup service resumed");
    }

    /// <summary>
    /// Performs a full scan and backup of all watched folders.
    /// Queues ALL files found regardless of their current backup status.
    /// Use PerformInitialSyncAsync for smarter syncing that skips already-backed-up files.
    /// </summary>
    public async Task PerformFullScanAsync(IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _databaseService.GetConfiguration();
        List<string> allFiles = new();

        StatusChanged?.Invoke(this, "Scanning folders...");
        Log("PerformFullScanAsync: Starting full scan of all watched folders");

        // Scan all watched folders
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
            Log($"PerformFullScanAsync: Found {files.Count} files in {folder.Path}");
        }

        StatusChanged?.Invoke(this, $"Found {allFiles.Count} files to process");

        // Queue all files for backup
        for (var i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var file = allFiles[i];
            progress?.Report((i + 1, allFiles.Count, file));

            _databaseService.QueueFileChange(new FileChangeEvent
            {
                FilePath = file,
                ChangeType = FileChangeType.Created,
                DetectedAt = DateTime.UtcNow
            });
        }

        StatusChanged?.Invoke(this, $"Queued {allFiles.Count} files for backup");
        Log($"PerformFullScanAsync: Complete - queued {allFiles.Count} files");
    }

    /// <summary>
    /// Performs an initial sync of all watched folders with Azure storage.
    /// Only queues files that are new or have changed since last backup.
    /// This is more efficient than PerformFullScanAsync for subsequent syncs.
    /// Uses bulk database lookups instead of per-file queries for performance at scale.
    /// </summary>
    /// <param name="progress">Progress reporter for UI updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary of sync results (total files, new files queued, unchanged files skipped)</returns>
    public async Task<InitialSyncResult> PerformInitialSyncAsync(
        IProgress<(int current, int total, string file, string status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log("PerformInitialSyncAsync: Starting initial sync");
        var config = _databaseService.GetConfiguration();
        InitialSyncResult result = new();
        List<string> allFiles = new();

        StatusChanged?.Invoke(this, "Scanning watched folders for sync...");

        // Phase 1: Scan all watched folders to find files
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            Log($"PerformInitialSyncAsync: Scanning folder {folder.Path}");
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
        }

        result.TotalFilesScanned = allFiles.Count;
        StatusChanged?.Invoke(this, $"Found {allFiles.Count} files - checking which need backup...");
        Log($"PerformInitialSyncAsync: Found {allFiles.Count} total files to check");

        // Phase 2: Bulk-load all backup records and pending paths into memory
        // This replaces N sequential DB lookups with 2 bulk queries
        StatusChanged?.Invoke(this, "Loading backup state from database...");
        var backedUpFiles = _databaseService.GetAllBackedUpFiles()
            .ToDictionary(f => f.LocalPath, f => f, StringComparer.OrdinalIgnoreCase);
        var pendingPaths = _databaseService.GetAllPendingChangePaths();
        Log($"PerformInitialSyncAsync: Loaded {backedUpFiles.Count} backup records and {pendingPaths.Count} pending paths");

        // Phase 3: Compare each file against in-memory lookup tables
        for (var i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = allFiles[i];
            var fileName = Path.GetFileName(filePath);

            try
            {
                // Check if file is already pending (in-memory set lookup)
                if (pendingPaths.Contains(filePath))
                {
                    result.AlreadyPending++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Already queued"));
                    continue;
                }

                // Get existing backup record (in-memory dictionary lookup)
                backedUpFiles.TryGetValue(filePath, out var existingBackup);

                if (existingBackup == null)
                {
                    // New file - never backed up
                    _databaseService.QueueFileChange(new FileChangeEvent
                    {
                        FilePath = filePath,
                        ChangeType = FileChangeType.Created,
                        DetectedAt = DateTime.UtcNow
                    });
                    result.NewFilesQueued++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "New - queued"));
                    Log($"PerformInitialSyncAsync: New file queued: {fileName}");
                }
                else if (existingBackup.Status == BackupStatus.Completed)
                {
                    // File was previously backed up - check if it changed
                    FileInfo fileInfo = new(filePath);

                    // Quick check: compare last modified time and size
                    if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified || 
                        fileInfo.Length != existingBackup.FileSize)
                    {
                        // File appears changed - queue for backup (hash will be verified during backup)
                        _databaseService.QueueFileChange(new FileChangeEvent
                        {
                            FilePath = filePath,
                            ChangeType = FileChangeType.Modified,
                            DetectedAt = DateTime.UtcNow
                        });
                        result.ModifiedFilesQueued++;
                        progress?.Report((i + 1, allFiles.Count, fileName, "Modified - queued"));
                        Log($"PerformInitialSyncAsync: Modified file queued: {fileName}");
                    }
                    else
                    {
                        // File unchanged
                        result.UnchangedFiles++;
                        progress?.Report((i + 1, allFiles.Count, fileName, "Unchanged"));
                    }
                }
                else if (existingBackup.Status == BackupStatus.Failed)
                {
                    // Retry previously failed file
                    _databaseService.QueueFileChange(new FileChangeEvent
                    {
                        FilePath = filePath,
                        ChangeType = FileChangeType.Modified,
                        DetectedAt = DateTime.UtcNow
                    });
                    result.RetriedFiles++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Retrying failed"));
                    Log($"PerformInitialSyncAsync: Retrying failed file: {fileName}");
                }
                else
                {
                    // Excluded or other status - skip
                    result.SkippedFiles++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Skipped"));
                }
            }
            catch (Exception ex)
            {
                Log($"PerformInitialSyncAsync: Error checking file {fileName}: {ex.Message}");
                result.ErrorFiles++;
                progress?.Report((i + 1, allFiles.Count, fileName, $"Error: {ex.Message}"));
            }
        }

        var queuedCount = result.NewFilesQueued + result.ModifiedFilesQueued + result.RetriedFiles;
        StatusChanged?.Invoke(this, 
            $"Sync complete: {queuedCount} files queued for backup, {result.UnchangedFiles} unchanged");
        Log($"PerformInitialSyncAsync: Complete - {queuedCount} queued, {result.UnchangedFiles} unchanged");


        return result;
    }

    /// <summary>
    /// Backs up a single file.
    /// </summary>
    public Task<bool> BackupFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return BackupFileAsync(filePath, progress: null, cancellationToken);
    }

    /// <summary>
    /// Backs up a single file with progress reporting.
    /// </summary>
    /// <param name="filePath">Path to the file to backup</param>
    /// <param name="progress">Reports byte-level progress (bytes completed, total bytes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> BackupFileAsync(
        string filePath, 
        IProgress<(long current, long total)>? progress,
        CancellationToken cancellationToken = default)
    {
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


            // Get file info
            FileInfo fileInfo = new(filePath);
            var fileHash = await _chunkingService.ComputeFileHashAsync(filePath, cancellationToken);

            // Check if file has changed
            var existingFile = _databaseService.GetBackedUpFile(filePath);
            if (existingFile != null && existingFile.FileHash == fileHash)
            {
                // File unchanged - report as complete
                progress?.Report((fileInfo.Length, fileInfo.Length));
                return true;
            }

            StatusChanged?.Invoke(this, $"Backing up: {Path.GetFileName(filePath)}");

            // Chunk the file
            var chunks = await _chunkingService.ChunkFileAsync(filePath, cancellationToken);

            // Determine which chunks need uploading
            var existingChunks = existingFile?.Chunks ?? [];
            var chunksToUpload = _chunkingService.GetChangedChunks(existingChunks, chunks);
            
            // For new files (no existing backup), skip existence checks - all chunks are new
            // This reduces API calls by 50% for new file uploads
            var isNewFile = existingFile == null;
            
            // Get the storage tier based on the watched folder configuration
            var storageTier = GetStorageTierForFile(filePath);
            Log($"BackupFileAsync: File is {(isNewFile ? "NEW" : "EXISTING")}, {chunksToUpload.Count} chunks to upload, tier={storageTier}");

            // Upload changed chunks with parallel processing for better bandwidth utilization
            long bytesUploaded = 0;
            var totalFileSize = fileInfo.Length;
            object uploadLock = new();
            
            // Use parallel uploads for files with multiple chunks
            if (chunksToUpload.Count > 1)
            {
                // Parallel upload with semaphore to limit concurrency
                using SemaphoreSlim semaphore = new(MaxParallelChunkUploads);
                var uploadTasks = chunksToUpload.Select(async chunk =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    var chunkData = Array.Empty<byte>();
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk, cancellationToken);

                        // Use direct upload for new files (skip existence check)
                        // Use regular upload for modified files (check for unchanged chunks)
                        chunk.BlobName = isNewFile
                            ? await _blobService.UploadChunkDirectAsync(chunkData, chunk.Hash, storageTier,
                                new Progress<long>(b =>
                                {
                                    lock (uploadLock)
                                    {
                                        bytesUploaded += b;
                                        progress?.Report((bytesUploaded, totalFileSize));
                                    }
                                }),
                                cancellationToken)
                            : await _blobService.UploadChunkAsync(chunkData, chunk.Hash, storageTier,
                                new Progress<long>(b =>
                                {
                                    lock (uploadLock)
                                    {
                                        bytesUploaded += b;
                                        progress?.Report((bytesUploaded, totalFileSize));
                                    }
                                }),
                                cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunkData);
                        semaphore.Release();
                    }
                }).ToList();
                
                await Task.WhenAll(uploadTasks);
            }
            else
            {
                // Single chunk - upload directly without parallelization overhead
                foreach (var chunk in chunksToUpload)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk, cancellationToken);
                    try
                    {
                        // Use direct upload for new files (skip existence check)
                        chunk.BlobName = isNewFile
                            ? await _blobService.UploadChunkDirectAsync(chunkData, chunk.Hash, storageTier,
                                new Progress<long>(b => 
                                {
                                    bytesUploaded += b;
                                    progress?.Report((bytesUploaded, totalFileSize));
                                }), 
                                cancellationToken)
                            : await _blobService.UploadChunkAsync(chunkData, chunk.Hash, storageTier,
                                new Progress<long>(b => 
                                {
                                    bytesUploaded += b;
                                    progress?.Report((bytesUploaded, totalFileSize));
                                }), 
                                cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunkData);
                    }
                }
            }

            // Update blob names for existing chunks
            foreach (var chunk in chunks.Where(c => !chunksToUpload.Contains(c)))
            {
                chunk.BlobName = $"chunks/{chunk.Hash}";
            }

            // Save file metadata
            // BlobName uses HMAC-SHA256 keyed by the derived encryption key for deterministic
            // naming that cannot be guessed without the password.
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
                    Log($"BackupFileAsync: Added {chunks.Count} chunk references for new file");
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
                    Log($"BackupFileAsync: Updated chunk references for modified file");
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

            return true;
        }
        catch (Exception ex)
        {
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
                    Log($"RunBackupLoopAsync: Processing {backups.Count} files in parallel (max {MaxParallelFileBackups})");

                    await Parallel.ForEachAsync(
                        backups,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = MaxParallelFileBackups,
                            CancellationToken = cancellationToken
                        },
                        async (change, ct) =>
                        {
                            await BackupFileAsync(change.FilePath, ct);
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

    /// <summary>
    /// Performs a mirror sync from a local folder to Azure backup.
    /// This will:
    /// 1. Backup files that are new or modified locally
    /// 2. Mark files in Azure as deleted if they no longer exist locally
    /// 3. Skip files that are identical
    /// </summary>
    /// <param name="localFolder">Local folder to sync from</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<MirrorSyncResult> MirrorSyncToAzureAsync(
        WatchedFolder localFolder,
        IProgress<(int current, int total, string file, string action)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localFolder);
        
        Log($"MirrorSyncToAzureAsync: Starting mirror sync from '{localFolder.Path}' to Azure");
        MirrorSyncResult result = new();

        StatusChanged?.Invoke(this, $"Mirror sync: scanning {localFolder.Path}");

        // Phase 1: Scan local folder for files
        var localFiles = await _fileWatcherService.ScanFolderAsync(localFolder, cancellationToken);
        HashSet<string> localFilePaths = new(localFiles, StringComparer.OrdinalIgnoreCase);

        Log($"MirrorSyncToAzureAsync: Found {localFiles.Count} local files");

        // Phase 2: Get existing backups for this folder
        var existingBackups = _databaseService.GetAllBackedUpFiles()
            .Where(f => f.LocalPath.StartsWith(localFolder.Path, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.LocalPath, StringComparer.OrdinalIgnoreCase);

        var totalOperations = localFiles.Count + existingBackups.Count;
        var currentOp = 0;

        // Phase 3: Backup new and modified files
        foreach (var localFilePath in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentOp++;

            var fileName = Path.GetFileName(localFilePath);

            try
            {
                if (existingBackups.TryGetValue(localFilePath, out var existingBackup))
                {
                    // File exists in backup - check if modified
                    FileInfo fileInfo = new(localFilePath);
                    
                    if (fileInfo.Length == existingBackup.FileSize &&
                        Math.Abs((fileInfo.LastWriteTimeUtc - existingBackup.LastModified).TotalSeconds) < 2)
                    {
                        // Quick check suggests file is unchanged
                        result.FilesUnchanged++;
                        progress?.Report((currentOp, totalOperations, fileName, "Unchanged"));
                        continue;
                    }
                }

                // File is new or modified - backup it
                progress?.Report((currentOp, totalOperations, fileName, "Backing up"));
                var success = await BackupFileAsync(localFilePath, cancellationToken);

                if (success)
                {
                    result.FilesTransferred++;
                    FileInfo fileInfo = new(localFilePath);
                    result.BytesTransferred += fileInfo.Length;
                }
                else
                {
                    result.FilesErrored++;
                    result.Errors.Add($"Failed to backup: {localFilePath}");
                }
            }
            catch (Exception ex)
            {
                result.FilesErrored++;
                result.Errors.Add($"Error backing up {localFilePath}: {ex.Message}");
                Log($"MirrorSyncToAzureAsync: Error backing up {localFilePath}: {ex.Message}");
            }
        }

        // Phase 4: Mark deleted files (files in backup but not locally)
        foreach (var (backupPath, backupFile) in existingBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentOp++;

            if (!localFilePaths.Contains(backupPath))
            {
                try
                {
                    progress?.Report((currentOp, totalOperations, Path.GetFileName(backupPath), "Marking deleted"));
                    
                    // Remove chunk references (this will delete orphaned chunks immediately)
                    if (_chunkIndexService != null)
                    {
                        var deletedChunks = await _chunkIndexService.RemoveFileReferencesAsync(
                            backupPath, cancellationToken);
                        if (deletedChunks > 0)
                        {
                            Log($"MirrorSyncToAzureAsync: Deleted {deletedChunks} orphaned chunks " +
                                $"for deleted file: {backupPath}");
                        }
                    }
                    
                    // Mark as excluded (deleted) but keep in Azure for potential restore
                    backupFile.Status = BackupStatus.Excluded;
                    _databaseService.SaveBackedUpFile(backupFile);
                    result.FilesDeleted++;
                    
                    Log($"MirrorSyncToAzureAsync: Marked as deleted: {backupPath}");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to mark deleted: {backupPath}: {ex.Message}");
                }
            }
        }

        StatusChanged?.Invoke(this, 
            $"Mirror sync complete: {result.FilesTransferred} backed up, {result.FilesDeleted} marked deleted, " +
            $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");
        
        Log($"MirrorSyncToAzureAsync: Complete - {result.FilesTransferred} transferred, " +
            $"{result.FilesDeleted} deleted, {result.FilesUnchanged} unchanged");

        return result;
    }

    /// <summary>
    /// Generates a preview of what a backup sync operation will do without making changes.
    /// This allows showing the user what will be uploaded before starting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of the backup operation</returns>
    public async Task<OperationPreview> PreviewBackupSyncAsync(CancellationToken cancellationToken = default)
    {
        Log("PreviewBackupSyncAsync: Generating backup preview");
        var config = _databaseService.GetConfiguration();
        
        OperationPreview preview = new()
        {
            OperationType = OperationType.Backup,
            OperationDescription = "Sync local files to Azure backup",
            SourceDescription = $"{config.WatchedFolders.Count(f => f.IsEnabled)} watched folder(s)",
            TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
        };

        // Scan all watched folders
        List<string> allFiles = new();
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
        }

        Log($"PreviewBackupSyncAsync: Found {allFiles.Count} files to check");
        
        // Get list of files that actually exist in Azure for validation
        HashSet<string>? azureFilePaths = null;
        if (_blobService.IsConnected)
        {
            try
            {
                var metadataBlobs = await _blobService.ListMetadataBlobsAsync(cancellationToken);
                azureFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var blobName in metadataBlobs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var metadata = await _blobService.DownloadFileMetadataAsync(blobName, cancellationToken);
                        if (metadata?.LocalPath != null)
                        {
                            azureFilePaths.Add(metadata.LocalPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"PreviewBackupSyncAsync: Error downloading metadata {blobName}: {ex.Message}");
                    }
                }
                Log($"PreviewBackupSyncAsync: Found {azureFilePaths.Count} files in Azure for validation");
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupSyncAsync: Could not fetch Azure file list for validation: {ex.Message}");
                // Continue without validation - will use local DB only
            }
        }

        // Compare each file with existing backup records
        foreach (var filePath in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileInfo fileInfo = new(filePath);
                if (!fileInfo.Exists) continue;

                var existingBackup = _databaseService.GetBackedUpFile(filePath);
                
                // If we have Azure file list, verify the file actually exists in Azure
                var actuallyExistsInAzure = azureFilePaths == null || azureFilePaths.Contains(filePath);
                
                // If local DB says file exists but Azure says it doesn't, treat as new
                if (existingBackup != null && !actuallyExistsInAzure)
                {
                    Log($"PreviewBackupSyncAsync: {Path.GetFileName(filePath)} - local DB has record but not in Azure, treating as new");
                    existingBackup = null;
                }

                if (existingBackup == null)
                {
                    // New file - never backed up
                    preview.FilesToCreate.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Create,
                        Reason = "New file - never backed up"
                    });
                }
                else if (existingBackup.Status == BackupStatus.Completed)
                {
                    // File was previously backed up - check if it changed
                    if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified ||
                        fileInfo.Length != existingBackup.FileSize)
                    {
                        preview.FilesToOverwrite.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Update,
                            Reason = fileInfo.Length != existingBackup.FileSize
                                ? $"Size changed ({existingBackup.FileSize} ? {fileInfo.Length})"
                                : "Modified since last backup"
                        });
                    }
                    else
                    {
                        preview.FilesToSkip.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Skip,
                            Reason = "Unchanged"
                        });
                    }
                }
                else if (existingBackup.Status == BackupStatus.Failed)
                {
                    preview.FilesToCreate.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Create,
                        Reason = "Retrying previously failed backup"
                    });
                }
                else
                {
                    preview.FilesToSkip.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Skip,
                        Reason = "Excluded or in-progress"
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupSyncAsync: Error checking {filePath}: {ex.Message}");
            }
        }

        Log($"PreviewBackupSyncAsync: Preview complete - {preview.CreateCount} new, " +
            $"{preview.OverwriteCount} modified, {preview.SkipCount} unchanged");

        return preview;
    }

    /// <summary>
    /// Generates a preview of what backing up specific files will do (simple overload).
    /// </summary>
    public Task<OperationPreview> PreviewBackupFilesAsync(
        IList<string> filePaths,
        CancellationToken cancellationToken)
    {
        return PreviewBackupFilesAsync(filePaths, null, cancellationToken);
    }

    /// <summary>
    /// Generates a preview of what backing up specific files will do.
    /// Cross-references with actual Azure metadata to ensure accuracy.
    /// </summary>
    /// <param name="filePaths">List of file paths to preview</param>
    /// <param name="azureFilePaths">Optional set of file paths that actually exist in Azure (for validation)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of the backup operation</returns>
    public Task<OperationPreview> PreviewBackupFilesAsync(
        IList<string> filePaths,
        ISet<string>? azureFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        Log($"PreviewBackupFilesAsync: Generating preview for {filePaths.Count} files" +
            (azureFilePaths != null ? $", validating against {azureFilePaths.Count} Azure files" : ""));


        var config = _databaseService.GetConfiguration();
        
        OperationPreview preview = new()
        {
            OperationType = OperationType.Backup,
            OperationDescription = $"Backup {filePaths.Count} selected file(s)",
            SourceDescription = "Selected local files",
            TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
        };

        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileInfo fileInfo = new(filePath);
                if (!fileInfo.Exists)
                {
                    Log($"PreviewBackupFilesAsync: File not found: {filePath}");
                    continue;
                }

                var existingBackup = _databaseService.GetBackedUpFile(filePath);
                
                // If we have Azure file list, verify the file actually exists in Azure
                // This prevents showing "overwrite" for files that were deleted from Azure
                var actuallyExistsInAzure = azureFilePaths == null || azureFilePaths.Contains(filePath);
                
                // If local DB says file exists but Azure says it doesn't, treat as new
                if (existingBackup != null && !actuallyExistsInAzure)
                {
                    Log($"PreviewBackupFilesAsync: {filePath} - local DB has record but not in Azure, treating as new");
                    existingBackup = null; // Treat as if never backed up
                }

                if (existingBackup == null)
                {
                    preview.FilesToCreate.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Create,
                        Reason = "New file - never backed up"
                    });
                }
                else if (existingBackup.Status == BackupStatus.Completed)
                {
                    if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified ||
                        fileInfo.Length != existingBackup.FileSize)
                    {
                        preview.FilesToOverwrite.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Update,
                            Reason = fileInfo.Length != existingBackup.FileSize
                                ? $"Size changed ({existingBackup.FileSize} ? {fileInfo.Length})"
                                : "Modified since last backup"
                        });
                    }
                    else
                    {
                        preview.FilesToSkip.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Skip,
                            Reason = "Already backed up and unchanged"
                        });
                    }
                }
                else if (existingBackup.Status == BackupStatus.Failed)
                {
                    preview.FilesToCreate.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Create,
                        Reason = "Retrying previously failed backup"
                    });
                }
                else
                {
                    preview.FilesToSkip.Add(new PreviewFileAction
                    {
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Skip,
                        Reason = "Excluded"
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupFilesAsync: Error checking {filePath}: {ex.Message}");
            }
        }

        Log($"PreviewBackupFilesAsync: Preview complete - {preview.CreateCount} new, " +
            $"{preview.OverwriteCount} modified, {preview.SkipCount} unchanged");

        return Task.FromResult(preview);
    }

    /// <summary>
    /// Backs up specific files to Azure using parallel file processing.
    /// </summary>
    /// <param name="filePaths">List of file paths to backup</param>
    /// <param name="progress">Progress reporter with file index, total files, file name, overall bytes, total bytes, current file bytes, current file size</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task BackupFilesAsync(
        IList<string> filePaths,
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        Log($"BackupFilesAsync: Starting parallel backup of {filePaths.Count} files (max {MaxParallelFileBackups} concurrent)");

        var totalFiles = filePaths.Count;
        long totalBytes = 0;
        long processedBytes = 0;
        int completedFiles = 0;

        // Calculate total bytes
        foreach (var filePath in filePaths)
        {
            try
            {
                FileInfo fileInfo = new(filePath);
                if (fileInfo.Exists)
                    totalBytes += fileInfo.Length;
            }
            catch
            {
                // Skip files we can't access
            }
        }

        await Parallel.ForEachAsync(
            filePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelFileBackups,
                CancellationToken = cancellationToken
            },
            async (filePath, ct) =>
            {
                var fileName = Path.GetFileName(filePath);

                try
                {
                    FileInfo fileInfo = new(filePath);
                    if (!fileInfo.Exists)
                    {
                        Log($"BackupFilesAsync: File not found, skipping: {filePath}");
                        return;
                    }

                    var currentFileSize = fileInfo.Length;
                    StatusChanged?.Invoke(this, $"Backing up: {fileName}");

                    // Track per-file byte deltas for accurate aggregate progress
                    long lastReportedFileBytes = 0;

                    Progress<(long current, long total)> fileProgress = new(p =>
                    {
                        var delta = p.current - Interlocked.Exchange(ref lastReportedFileBytes, p.current);
                        if (delta > 0)
                            Interlocked.Add(ref processedBytes, delta);

                        progress?.Report((
                            Volatile.Read(ref completedFiles), totalFiles, fileName,
                            Interlocked.Read(ref processedBytes), totalBytes,
                            p.current, currentFileSize));
                    });

                    var success = await BackupFileAsync(filePath, fileProgress, ct);

                    if (success)
                    {
                        // Reconcile any remaining bytes not yet reported by progress callbacks
                        var finalReported = Interlocked.Exchange(ref lastReportedFileBytes, currentFileSize);
                        var remaining = currentFileSize - finalReported;
                        if (remaining > 0)
                            Interlocked.Add(ref processedBytes, remaining);

                        var done = Interlocked.Increment(ref completedFiles);
                        Log($"BackupFilesAsync: [{done}/{totalFiles}] Successfully backed up: {fileName}");
                        _databaseService.RemovePendingChange(filePath);

                        progress?.Report((
                            done, totalFiles, fileName,
                            Interlocked.Read(ref processedBytes), totalBytes,
                            currentFileSize, currentFileSize));
                    }
                    else
                    {
                        Log($"BackupFilesAsync: Failed to backup: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"BackupFilesAsync: Error backing up {filePath}: {ex.Message}");
                    ErrorOccurred?.Invoke(this, $"Failed to backup {fileName}: {ex.Message}");
                }
            });

        StatusChanged?.Invoke(this, $"Backup complete: {totalFiles} files processed");
        Log($"BackupFilesAsync: Complete - {totalFiles} files processed, {Interlocked.Read(ref processedBytes)} bytes");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _backupCts?.Dispose();
    }
}

public class BackupProgressEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public long BytesUploaded { get; set; }
    public int ChunksUploaded { get; set; }
    public int TotalChunks { get; set; }
}
