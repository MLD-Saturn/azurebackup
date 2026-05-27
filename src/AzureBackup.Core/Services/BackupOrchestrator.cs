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
    // For a file with many chunks, upload up to 6 chunks simultaneously
    private const int MaxParallelChunkUploads = 6;

    // File-level parallelism for multi-file operations.
    // B27 (was 8): bumped to 16 after TwoTierFileSplitBigScaleBenchmark
    // on the AMD EPYC 7763 / 16-logical-core production hardware showed
    // 16-way wins -22% on production-scale-3000 and -27% on
    // media-library-500 against the previous 8-way default, with no
    // regression on huge-outlier-mixed (where the workload is dominated
    // by two giant files and file-level parallelism is irrelevant).
    // 32-way was also measured and was within noise of 16-way, so 16
    // is the crossover where the gain has stopped paying. The
    // MemoryBudget still caps total in-flight memory regardless of
    // file count, and the recommended UI default of MemoryLimitMB=8192
    // (set in B27) is sized to leave headroom for 16-way at 6 chunks
    // per file at the 64 MB MaxChunkSize ceiling.
    private const int MaxParallelFileBackups = 16;

    /// <summary>
    /// B25-bench-2: optional per-instance override for
    /// <see cref="MaxParallelChunkUploads"/>. When non-null the
    /// per-file upload pipeline (channel size + consumer count) uses
    /// this value instead of the constant. Intended for the
    /// <c>AdaptiveChunkConcurrencyBenchmark</c> harness so it can
    /// sweep the per-file chunk concurrency without forking the
    /// orchestrator. Defaults to <c>null</c> -- production behaviour
    /// is unchanged when the property is left unset.
    /// </summary>
    /// <remarks>
    /// This is a deliberately minimal seam. The benchmark sets it
    /// once on the instance immediately after construction and never
    /// changes it during a backup. Concurrent set during an in-flight
    /// <c>BackupFileAsync</c> would race; the production call sites
    /// snapshot the effective value at the top of each method, so a
    /// race produces a stale-but-valid value rather than torn state.
    /// </remarks>
    public int? MaxParallelChunkUploadsOverride { get; set; }

    /// <summary>
    /// B25-bench-2: optional per-instance override for
    /// <see cref="MaxParallelFileBackups"/>. When non-null the
    /// file-level <c>Parallel.ForEachAsync</c> uses this value
    /// instead of the constant. Intended for the
    /// <c>TwoTierFileSplitBenchmark</c> harness. Defaults to
    /// <c>null</c> -- production behaviour is unchanged when the
    /// property is left unset.
    /// </summary>
    public int? MaxParallelFileBackupsOverride { get; set; }

    /// <summary>
    /// W5 Phase 1: optional per-instance override for the
    /// <see cref="BackupMemoryReporter"/> sampling cadence. The
    /// production default of 30 s (see the reporter's class summary)
    /// is appropriate for long-running backups but loses sub-30s
    /// peaks during a benchmark iteration. Setting this to e.g.
    /// <c>TimeSpan.FromMilliseconds(100)</c> gives the
    /// <c>MemoryFidelityCollector</c> in the benchmark project a
    /// dense enough sample stream to derive defensible
    /// <c>MaxUnaccounted_MB</c> and <c>OvershootRatio</c> columns.
    /// Defaults to <c>null</c> -- production behaviour is unchanged
    /// when the property is left unset, and the reporter falls back
    /// to its 30 s default. Intended for benchmarks only; do NOT
    /// set this on a production orchestrator instance, the cost of
    /// 10 samples / second is negligible per-call but the resulting
    /// log volume is not.
    /// </summary>
    public TimeSpan? MemoryReporterIntervalOverride { get; set; }

    /// <summary>
    /// Effective per-file chunk-upload concurrency. Reads
    /// <see cref="MaxParallelChunkUploadsOverride"/> when set,
    /// falls back to the production constant. Snapshotted once at
    /// the top of <c>BackupFileAsync</c> so the value cannot change
    /// mid-pipeline.
    /// </summary>
    private int EffectiveMaxParallelChunkUploads
        => MaxParallelChunkUploadsOverride ?? MaxParallelChunkUploads;

    /// <summary>
    /// Effective file-level concurrency. Reads
    /// <see cref="MaxParallelFileBackupsOverride"/> when set, falls
    /// back to the production constant.
    /// </summary>
    private int EffectiveMaxParallelFileBackups
        => MaxParallelFileBackupsOverride ?? MaxParallelFileBackups;

    /// <summary>
    /// B54 (W3 Phase C): per-file residency estimate used by
    /// <see cref="ComputeEffectiveFileConcurrency"/> to decide how
    /// far the file-level fan-out can scale before the
    /// <see cref="MemoryBudget"/> would be over-subscribed.
    /// <para>
    /// Sizing: the worst-case per-file producer-side charge is
    /// <c>MaxParallelChunkUploads (6) × MaxChunkSize (64 MB)</c>
    /// = 384 MB of in-flight chunk payload, plus the per-operation
    /// <see cref="CdcBufferOverhead"/> (16 MB) and assorted
    /// per-file pipeline overhead (rolling-hash window, scratch
    /// buffers, channel slots, IncrementalHash state) which together
    /// round up to roughly 512 MB. That value also matches the
    /// existing B27 sizing comment on <see cref="MaxParallelFileBackups"/>:
    /// the recommended <c>MemoryLimitMB=8192</c> default was chosen
    /// to leave headroom for 16-way × 6 chunks per file, i.e.
    /// 8192 ÷ 16 = 512 MB per file. This constant therefore makes the
    /// formal scaling rule agree with the historical hand-picked one
    /// at the production default; it only changes behaviour at smaller
    /// budgets where the configured ceiling no longer fits.
    /// </para>
    /// <para>
    /// B55 note: the producer-side charge per chunk now ALSO includes
    /// the Azure SDK staging estimate from
    /// <c>AzureBlobService.EstimateUploadStagingBytes</c>, so the
    /// effective per-chunk charge is larger than the
    /// payload+encrypt sum that drove the pre-B55 math. The
    /// per-file estimate is intentionally NOT widened in lockstep;
    /// raising it would over-throttle file fan-out and re-introduce
    /// the pre-B27 admission stalls. Instead, the budget itself
    /// admits fewer concurrent chunks per file when staging is
    /// charged, which converges to the same in-flight residency
    /// without the orchestrator-side over-correction. The file-level
    /// fan-out only needs an estimate accurate enough to prevent
    /// over-admission at the file boundary; finer per-chunk control
    /// is the budget's job.
    /// </para>
    /// <para>
    /// The estimate is intentionally conservative -- the real
    /// budget enforcement still happens inside
    /// <see cref="MemoryBudget"/>, which throttles admission when the
    /// actual in-flight bytes approach the cap. Reducing fan-out at
    /// this level only prevents the orchestrator from STARTING work it
    /// would immediately have to stall on, and from inflating Azure
    /// SDK staging residency by spinning up too many parallel uploads
    /// against a cap they cannot all fit under.
    /// </para>
    /// </summary>
    private const long EstimatedPerFileResidencyBytes = 512L * 1024 * 1024;

    /// <summary>
    /// B54 (W3 Phase C): clamp the configured file-level concurrency
    /// against the active <see cref="MemoryBudget"/> so a small
    /// memory limit reduces the file-level fan-out instead of
    /// admitting many files that would all immediately stall.
    /// <para>
    /// Returns <see cref="long.MaxValue"/>-equivalent (i.e. the full
    /// configured ceiling) when the budget is unlimited.
    /// Otherwise returns
    /// <c>clamp(budget.TotalBytes / EstimatedPerFileResidencyBytes,
    /// 1, configuredCeiling)</c>. The floor of one preserves the
    /// invariant that backups must always make some forward progress
    /// even on a tiny budget; the budget itself will throttle
    /// admission inside the per-file pipeline.
    /// </para>
    /// </summary>
    /// <param name="budget">
    /// Active memory budget for the operation. The cap is derived
    /// from this budget so a slider change for a future operation
    /// produces a different effective concurrency without touching
    /// any constants.
    /// </param>
    /// <param name="configuredCeiling">
    /// The hard ceiling from
    /// <see cref="EffectiveMaxParallelFileBackups"/> (which already
    /// honours the <see cref="MaxParallelFileBackupsOverride"/>
    /// benchmark seam). The result is never larger than this value.
    /// </param>
    internal static int ComputeEffectiveFileConcurrency(MemoryBudget budget, int configuredCeiling)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredCeiling);

        if (budget.IsUnlimited)
            return configuredCeiling;

        var byBudget = budget.TotalBytes / EstimatedPerFileResidencyBytes;
        if (byBudget < 1)
            return 1;
        if (byBudget > configuredCeiling)
            return configuredCeiling;
        return (int)byBudget;
    }

    // Batch size for the background backup monitoring loop
    private const int BackupLoopBatchSize = 50;

    // Memory budget overhead for fixed (non-chunk) per-file allocations.
    // <para>
    // Pre-B30 this was 128 MB, sized to cover the chunking service's
    // peak per-file payload buffer rental. After B30 the producer's
    // chunk buffer is charged exactly via
    // <c>ChunkingService.AcquireChunkBufferAsync</c>, so the only
    // residual fixed overhead per backup operation is the 64 KB scratch
    // buffer, file-stream OS read-ahead, the rolling-hash window, and
    // the IncrementalHash state -- all small and fixed-size. 16 MB is
    // a generous safety margin that absorbs SqliteBackend connection
    // overhead, ChannelWriter slot allocations, and any other small
    // managed allocations that touch the budget's nominal headroom but
    // are not visible to the budget itself.
    // </para>
    private const long CdcBufferOverhead = 16L * 1024 * 1024;

    /// <summary>
    /// B52: derive the global byte cap for an operation-scoped
    /// <see cref="LargeChunkBufferPool"/> from the active
    /// <see cref="MemoryBudget"/>. The pool was previously sized only
    /// by its per-bucket cap, which under default settings allows
    /// ~15.5 GB of LOH residency -- well above any real-world
    /// <c>MemoryLimitMB</c> setting. After B52 the pool's cached
    /// residency is bounded by 25 percent of the configured budget,
    /// so a user with an 8 GB budget caps the recycler at ~2 GB and
    /// the remaining 6 GB stays available for in-flight chunk work.
    /// <para>
    /// 25 percent was chosen because the pool exists to recycle
    /// buffers across chunks, not to hold work in flight; the
    /// in-flight work is what the <see cref="MemoryBudget"/> itself
    /// throttles. A higher fraction would let the cache grow at the
    /// expense of admission, increasing stalls; a lower fraction
    /// would force more fresh allocations and undo the recycler's
    /// purpose. A floor of one largest-bucket-size keeps the cap
    /// useful even on a tiny budget.
    /// </para>
    /// <para>
    /// Unlimited budgets fall through to <see cref="long.MaxValue"/>
    /// so existing benchmark and test harnesses that opt out of the
    /// budget are unaffected.
    /// </para>
    /// </summary>
    internal static long ComputePoolCapBytes(MemoryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.IsUnlimited)
            return long.MaxValue;
        var quarter = budget.TotalBytes / 4;
        // Floor: at least one buffer of the largest bucket so the
        // cap is never so small that the pool is effectively
        // disabled on a small budget.
        var floor = 256L * 1024 * 1024;
        return Math.Max(quarter, floor);
    }

    /// <summary>
    /// B69 (W5 Phase 3 Commit 1): derive the global byte cap for an
    /// operation-scoped <see cref="ChunkBufferPool"/> constructed with
    /// <see cref="ChunkBufferPool.SmallChunkBucketSizes"/> from the
    /// active <see cref="MemoryBudget"/>. The small-chunk pool's
    /// cached residency is bounded by 12.5 percent of the configured
    /// budget so the combined ceiling for the small-chunk and
    /// large-chunk recyclers stays below 40 percent of the budget
    /// (25 percent for the large pool from
    /// <see cref="ComputePoolCapBytes(MemoryBudget)"/> plus 12.5
    /// percent here), leaving the majority of the budget available
    /// for in-flight chunk work.
    /// <para>
    /// 12.5 percent was chosen because the small-chunk path's
    /// worst-case residency is much lower than the large-chunk path's
    /// -- the small-chunk buckets cap at 16 MB while the large-chunk
    /// buckets reach 256 MB -- so the small pool needs proportionally
    /// less headroom to hit a useful hit-rate. A floor of one
    /// largest-small-bucket-size (16 MB) x 4 = 64 MB keeps the cap
    /// useful even on a tiny budget.
    /// </para>
    /// <para>
    /// Unlimited budgets fall through to <see cref="long.MaxValue"/>
    /// so existing benchmark and test harnesses that opt out of the
    /// budget are unaffected.
    /// </para>
    /// </summary>
    internal static long ComputeSmallPoolCapBytes(MemoryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.IsUnlimited)
            return long.MaxValue;
        var eighth = budget.TotalBytes / 8;
        var floor = 64L * 1024 * 1024;
        return Math.Max(eighth, floor);
    }

    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Raised when an Azure operation fails with an authentication / authorization error
    /// (HTTP 401 or 403, or an <c>AuthenticationFailedException</c> from the Azure SDK).
    /// Subscribers (the UI) should prompt the user to re-authenticate. The cached
    /// credential is cleared before this event fires.
    /// </summary>
    public event EventHandler<AzureAuthenticationException>? AuthenticationFailed;

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

    /// <summary>
    /// Optional throughput metrics logger. When set, per-file and operation-level
    /// metrics are recorded to JSONL files for post-hoc performance analysis.
    /// </summary>
    public ThroughputMetrics? Metrics { get; set; }

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
    /// <param name="password">Password material, passed as <see cref="ReadOnlyMemory{T}"/>
    /// so the caller can keep the plaintext in a <c>char[]</c> and zero it after use.</param>
    public async Task<bool> InitializeAsync(ReadOnlyMemory<char> password)
    {
        // B15: emit a synchronous DiagnosticLog event BEFORE any
        // allocation so a downstream OOM still leaves a "we got here"
        // breadcrumb in the file log. The Log() helper is gated on
        // [Conditional("DIAGNOSTICLOG")] so this is free in Release.
        Log("InitializeAsync: ENTRY");

        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));
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
            PasswordValidator.Validate(password.Span);

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
    /// Legacy <c>string</c> overload of <see cref="InitializeAsync(ReadOnlyMemory{char})"/>.
    /// Prefer the span/memory overload so the plaintext password does not linger on the managed heap.
    /// </summary>
    public Task<bool> InitializeAsync(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return InitializeAsync(password.AsMemory());
    }

    /// <summary>
    /// Invalidates the cached Azure credential after an authentication / authorization
    /// failure and raises the <see cref="AuthenticationFailed"/> event so the UI can
    /// prompt the user to re-authenticate.
    /// </summary>
    /// <remarks>
    /// Safe to call multiple times. After this call, any Azure operation will fail
    /// with a connection error until the user signs in again.
    /// </remarks>
    public void InvalidateAzureCredential(AzureAuthenticationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log($"InvalidateAzureCredential: Clearing cached credential after {exception.Status} {exception.ErrorCode}");

        _credential = null;

        try
        {
            // Mark config as no longer authenticated so the UI reflects reality.
            var config = _databaseService.GetConfiguration();
            if (config.IsEntraIdAuthenticated)
            {
                config.IsEntraIdAuthenticated = false;
                _databaseService.SaveConfiguration(config);
            }
        }
        catch (Exception ex)
        {
            Log($"InvalidateAzureCredential: Could not update config: {ex.Message}");
        }

        AuthenticationFailed?.Invoke(this, exception);
        ErrorOccurred?.Invoke(this,
            "Azure authentication failed. Please sign in again from Settings.");
    }

    /// <summary>
    /// Returns <c>true</c> if the given exception indicates an Azure auth/authz failure.
    /// Callers in backup/restore loops use this to route via <see cref="InvalidateAzureCredential"/>.
    /// </summary>
    internal static bool TryExtractAuthFailure(Exception ex, out AzureAuthenticationException? auth)
    {
        if (ex is AzureAuthenticationException a)
        {
            auth = a;
            return true;
        }
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (TryExtractAuthFailure(inner, out auth))
                    return true;
            }
        }
        if (ex.InnerException != null)
        {
            return TryExtractAuthFailure(ex.InnerException, out auth);
        }
        auth = null;
        return false;
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
    /// B50: quarantines a corrupt local catalog database file by
    /// renaming it (and its companion <c>-wal</c>, <c>-shm</c>,
    /// <c>-journal</c>, and salt artefacts) to a timestamped
    /// <c>.quarantine-yyyyMMdd-HHmmss</c> suffix beside the original.
    /// Stops any running backup, clears in-memory secrets, and lets
    /// the next unlock create a fresh catalog at the same path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ResetApplicationAsync"/>: that
    /// destroys the catalog because the user explicitly chose to
    /// start over. Quarantine is the recovery path for an unreadable
    /// catalog the user did NOT ask to delete; the original bytes
    /// stay on disk for forensic inspection.
    /// </para>
    /// <para>
    /// The encrypted connection string lives inside the quarantined
    /// catalog and cannot be recovered without unlocking it. The user
    /// must re-enter the connection string (and storage account /
    /// container / watched folders) by hand once the fresh catalog is
    /// created. The agent treats the encrypted connection string as
    /// unrecoverable on this path by design.
    /// </para>
    /// </remarks>
    /// <param name="databasePath">Path to the corrupt catalog file.</param>
    /// <returns>
    /// Result describing the quarantined main DB path and any
    /// companion files that could not be moved.
    /// </returns>
    public async Task<QuarantineResult> QuarantineCorruptCatalogAsync(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (_isRunning)
        {
            await StopAsync();
        }

        _encryptionService.ClearKey();
        _credential = null;

        var result = _databaseService.QuarantineAndClose(databasePath);

        StatusChanged?.Invoke(
            this,
            $"Catalog quarantined to {result.QuarantinedDatabasePath}. " +
            "Set a new password and re-enter your Azure connection details to continue.");

        return result;
    }

    /// <summary>
    /// B51: rebuilds a fresh catalog at <paramref name="freshDatabasePath"/>
    /// from a previously quarantined catalog plus user-supplied Azure
    /// connection details. The quarantined catalog is opened read-only to
    /// extract the in-database <c>PasswordSalt</c> -- the only field that
    /// cannot be recovered from Azure or re-entered by the user. Every
    /// other piece of state (connection string, container name, watched
    /// folders, backed-up file graph) is either provided by the caller or
    /// reconstructed from Azure metadata via
    /// <c>ChunkIndexService.RebuildIndexFromAzureAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rebuild is destructive on the active catalog at
    /// <paramref name="freshDatabasePath"/>: any existing file at that
    /// path is quarantined first (so the user can audit anything that
    /// landed there post-recovery) before a fresh catalog is initialised.
    /// The quarantined input files are NOT modified.
    /// </para>
    /// <para>
    /// Distinguishing wrong passwords from partial decrypts is delegated
    /// to the SQLCipher validate-key probe inside
    /// <c>SqliteBackend.OpenAndUnlockCore</c>. A wrong password surfaces
    /// as <see cref="Models.InvalidPasswordException"/> from this method;
    /// a wrong Azure connection string surfaces as a connectivity error
    /// from <c>ChunkIndexService.RebuildIndexFromAzureAsync</c>.
    /// </para>
    /// </remarks>
    /// <param name="quarantinedDatabasePath">Path to the quarantined catalog (the
    /// <c>.quarantine-yyyyMMdd-HHmmss</c> file that <c>QuarantineCorruptCatalogAsync</c>
    /// created). Opened read-only.</param>
    /// <param name="quarantinedSaltPath">Path to the matching quarantined salt sidecar
    /// (the <c>.salt.quarantine-yyyyMMdd-HHmmss</c> file).</param>
    /// <param name="password">The password the quarantined catalog was protected
    /// with. The same password becomes the new fresh-catalog password.</param>
    /// <param name="connectionString">Azure storage account connection string.
    /// Re-encrypted into the fresh catalog with the recovered key.</param>
    /// <param name="containerName">Azure blob container that holds the backed-up
    /// chunks and metadata. Re-saved into the fresh catalog.</param>
    /// <param name="freshDatabasePath">Where to write the fresh catalog.
    /// Typically <c>AppMode.DatabasePath</c>.</param>
    /// <param name="progress">Optional progress reporter forwarded to the
    /// Azure rebuild phase.</param>
    /// <param name="cancellationToken">Cancellation token. Honoured by the
    /// Azure rebuild phase; the read-only extraction phase is fast and
    /// not cancellable.</param>
    /// <exception cref="ArgumentException">A path or password is null/whitespace.</exception>
    /// <exception cref="FileNotFoundException">The quarantined DB or salt is missing.</exception>
    /// <exception cref="Models.InvalidPasswordException">The password does not match the quarantined catalog.</exception>
    /// <exception cref="InvalidOperationException">The quarantined catalog has no
    /// <c>password_salt</c> recorded -- nothing was ever encrypted with it, so
    /// there is nothing to recover. The user should run a normal first-run setup
    /// instead.</exception>
    public async Task RebuildFromQuarantinedCatalogAsync(
        string quarantinedDatabasePath,
        string quarantinedSaltPath,
        ReadOnlyMemory<char> password,
        string connectionString,
        string containerName,
        string freshDatabasePath,
        IProgress<(int processed, int total, string currentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinedDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinedSaltPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(freshDatabasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (_chunkIndexService == null)
        {
            throw new InvalidOperationException(
                "BackupOrchestrator.SetChunkIndexService(...) must be called before " +
                "RebuildFromQuarantinedCatalogAsync; the rebuild needs the chunk-index " +
                "service to repopulate the catalog from Azure metadata.");
        }

        Log("RebuildFromQuarantinedCatalogAsync: starting");
        StatusChanged?.Invoke(this, "Reading password salt from quarantined catalog...");

        // Phase 1: extract the in-DB password_salt from the quarantined
        // catalog. The validate-key probe inside the read-only open is the
        // single oracle for password correctness.
        var recoveredSalt = Backends.SqliteBackend.ReadPasswordSaltFromQuarantinedCatalog(
            quarantinedDatabasePath, quarantinedSaltPath, password.Span);
        if (recoveredSalt == null)
        {
            throw new InvalidOperationException(
                "Quarantined catalog has no password_salt recorded -- it was never used " +
                "to encrypt any Azure data. There is nothing to rebuild from. Run a normal " +
                "first-run setup against a fresh catalog instead.");
        }
        Log("RebuildFromQuarantinedCatalogAsync: recovered PasswordSalt from quarantined catalog");

        // Phase 2: stop any running work, drop in-memory secrets, and make
        // sure the active catalog path is free. If something already exists
        // at freshDatabasePath we quarantine it first so the user can audit
        // it later -- never overwrite or delete a catalog file silently.
        if (_isRunning)
        {
            Log("RebuildFromQuarantinedCatalogAsync: stopping running backup");
            await StopAsync();
        }
        _encryptionService.ClearKey();
        _credential = null;

        if (LocalDatabaseService.DatabaseExists(freshDatabasePath))
        {
            Log($"RebuildFromQuarantinedCatalogAsync: existing catalog at {freshDatabasePath} -- quarantining it first");
            StatusChanged?.Invoke(this, "Existing catalog at the fresh path -- quarantining it first...");
            _ = _databaseService.QuarantineAndClose(freshDatabasePath);
        }
        else
        {
            // Defensive close -- the active catalog might be open against
            // a different path (e.g. portable mode quirk during testing).
            _databaseService.Close();
        }

        // Phase 3: initialise a fresh catalog at the active path with the
        // user's password. This generates a NEW (throwaway) PasswordSalt
        // and PasswordVerificationHash; we overwrite both with values
        // derived from the recovered salt below so the new catalog can
        // decrypt Azure blobs that were encrypted with the old salt.
        StatusChanged?.Invoke(this, "Creating fresh catalog at the active path...");
        _databaseService.Initialize(freshDatabasePath, password.Span);
        Log("RebuildFromQuarantinedCatalogAsync: fresh catalog initialized");

        // Phase 4: replace the throwaway salt with the recovered one and
        // recompute the verification hash so future unlocks succeed
        // against the same password + recovered salt pair.
        var freshConfig = _databaseService.GetConfiguration();
        freshConfig.PasswordSalt = recoveredSalt;
        freshConfig.PasswordVerificationHash =
            await _encryptionService.CreatePasswordVerificationHashAsync(password, recoveredSalt);
        freshConfig.FailedLoginAttempts = 0;
        freshConfig.LockoutUntilUtc = null;
        _databaseService.SaveConfiguration(freshConfig);
        Log("RebuildFromQuarantinedCatalogAsync: fresh catalog reseeded with recovered PasswordSalt");

        // Phase 5: derive the Azure encryption key from the recovered
        // salt + user password and arm the encryption service so the
        // upcoming Azure I/O can decrypt blobs that were encrypted with
        // the old catalog's key.
        StatusChanged?.Invoke(this, "Deriving Azure encryption key...");
        var key = await _encryptionService.DeriveKeyAsync(password, recoveredSalt);
        try
        {
            _encryptionService.Initialize(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        Log("RebuildFromQuarantinedCatalogAsync: encryption service armed with recovered key");

        // Phase 6: persist the user-supplied Azure connection string and
        // container name (re-encrypted with the recovered key) and open
        // the live blob connection. SaveConnectionStringAsync handles
        // both halves of that work.
        StatusChanged?.Invoke(this, "Connecting to Azure storage...");
        await SaveConnectionStringAsync(connectionString, containerName);

        // Phase 7: rebuild the catalog from Azure metadata. This wipes
        // the placeholder rows the fresh Initialize created and walks
        // every metadata blob to repopulate chunk_index, chunk_file_refs,
        // and the backed-up files graph. Cancellation is honoured here.
        StatusChanged?.Invoke(this, "Rebuilding catalog from Azure metadata...");
        await _chunkIndexService.RebuildIndexFromAzureAsync(progress, cancellationToken);

        StatusChanged?.Invoke(this,
            "Catalog rebuild from quarantined source complete. The fresh catalog can be " +
            "unlocked with the same password you used for the quarantined one.");
        Log("RebuildFromQuarantinedCatalogAsync: complete");
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
        return BackupFileAsync(filePath, progress: null, memoryBudget: null, largeChunkPool: null, smallChunkPool: null, forceReupload: false, cancellationToken);
    }

    /// <summary>
    /// Backs up a single file with progress reporting.
    /// </summary>
    /// <param name="filePath">Path to the file to backup</param>
    /// <param name="progress">Reports byte-level progress (bytes completed, total bytes)</param>
    /// <param name="memoryBudget">Optional memory budget for throttling parallel chunk uploads.
    /// When null, upload parallelism is unbounded (existing behavior).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task<bool> BackupFileAsync(
        string filePath,
        IProgress<(long current, long total)>? progress,
        MemoryBudget? memoryBudget,
        CancellationToken cancellationToken = default)
        => BackupFileAsync(filePath, progress, memoryBudget, largeChunkPool: null, smallChunkPool: null, forceReupload: false, cancellationToken);

    /// <summary>
    /// B37 overload preserved for callers that supply a large-chunk
    /// pool but no small-chunk pool. New B69 callers should prefer
    /// the overload that accepts both pools.
    /// </summary>
    public Task<bool> BackupFileAsync(
        string filePath,
        IProgress<(long current, long total)>? progress,
        MemoryBudget? memoryBudget,
        ChunkBufferPool? largeChunkPool,
        bool forceReupload = false,
        CancellationToken cancellationToken = default)
        => BackupFileAsync(filePath, progress, memoryBudget, largeChunkPool, smallChunkPool: null, forceReupload, cancellationToken);

    /// <summary>
    /// B37 overload: as above, plus an optional
    /// <see cref="ChunkBufferPool"/> shared across the operation.
    /// When supplied, large-chunk allocations flow through the bounded
    /// LOH recycler instead of the GC, eliminating the gen-2 retention
    /// pressure that B36 surfaced after B30/B33/B34 landed. Pass
    /// <c>null</c> to keep the pre-B37 GC-managed behaviour for tests
    /// and ad-hoc single-file callers.
    /// <para>
    /// B42: pass <paramref name="forceReupload"/>=<c>true</c> to bypass
    /// the metadata-skip fast path AND the per-chunk dedup filter, so
    /// every chunk in the file is re-encrypted and re-uploaded with
    /// overwrite semantics. This is the contract used by
    /// <c>IntegrityCheckService</c>'s auto-repair path and the manual
    /// Repair / Force Full Scan UI commands. Production hot paths
    /// (file watcher, MirrorSyncToAzureAsync, normal scheduled backups)
    /// MUST keep this <c>false</c> -- forcing re-upload defeats dedup
    /// and re-encrypts every byte of every file.
    /// </para>
    /// </summary>
    public async Task<bool> BackupFileAsync(
        string filePath,
        IProgress<(long current, long total)>? progress,
        MemoryBudget? memoryBudget,
        ChunkBufferPool? largeChunkPool,
        ChunkBufferPool? smallChunkPool,
        bool forceReupload = false,
        CancellationToken cancellationToken = default)
    {
        var diag = new FileOperationDiagnostics(filePath, "Backup", DiagnosticsDirectory);
        using var _ = diag.SetAmbient();
        var fileStopwatch = Stopwatch.StartNew();
        // B25-bench-2: snapshot the effective per-file chunk concurrency once
        // so the override cannot change mid-pipeline. Default is the
        // production constant (6) when no override is set.
        var maxParallelChunkUploads = EffectiveMaxParallelChunkUploads;
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
            // B42: forceReupload bypasses this fast path so the integrity-check auto-repair
            // and the manual Repair command can re-upload bytes that the local DB believes
            // are present but Azure has lost or corrupted.
            if (!forceReupload &&
                existingFile != null && existingFile.Status == BackupStatus.Completed &&
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
            // B42: when forceReupload is true, treat every chunk as new so the chunker
            // streams all chunks (not just deltas) and the per-chunk dedup filter below
            // becomes a pass-through.
            var existingHashes = forceReupload
                ? new HashSet<string>(StringComparer.Ordinal)
                : existingChunks.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);

            // For new files (no existing backup), skip existence checks - all chunks are new
            // B42: forceReupload also takes the new-file path so UploadChunkDirectAsync is
            // used unconditionally, overwriting any stale Azure-side blob.
            var isNewFile = existingFile == null || forceReupload;

            // Get the storage tier based on the watched folder configuration
            var storageTier = GetStorageTierForFile(filePath);

            // Pipeline: CDC + filtered upload in a single file open.
            // The bounded channel provides backpressure — the producer blocks when
            // MaxParallelChunkUploads consumers are busy uploading.
            var channel = Channel.CreateBounded<ChunkPayload>(new BoundedChannelOptions(maxParallelChunkUploads)
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            // Phase 6 / discovered-#2: pad each hot atomic counter onto its own
            // cache line. Without padding, MaxParallelChunkUploads consumers all
            // contend for the cache line containing both bytesUploaded and
            // chunksUploadedCount, producing measurable ping-pong on x64.
            PaddedLong bytesUploaded = default;
            PaddedLong chunksUploadedCount = default;
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
                    // B30: pass the shared memoryBudget through to the
                    // chunking service so producer-side allocations are
                    // charged at rent time. The consumer below no longer
                    // re-charges -- it only releases the amount the
                    // producer charged, which is carried on the payload.
                    // B37: pass the operation-scoped large-chunk pool so
                    // large-chunk allocations flow through the bounded
                    // LOH recycler instead of `new byte[]`, removing the
                    // gen-2 retention pressure from the heap. The
                    // consumer mirrors the producer's pool decision via
                    // `ChunkPayload.BufferPool` so the buffer goes back
                    // to the right place after upload.
                    // B69: pass the operation-scoped small-chunk pool so
                    // small-chunk allocations flow through the owned
                    // bucket bags instead of ArrayPool<byte>.Shared,
                    // eliminating the per-core tier-cache residency that
                    // pre-B69 lived outside the active MemoryBudget. The
                    // same ChunkPayload.BufferPool field carries the
                    // small pool when the small path was taken; the
                    // consumer's return cascade does not need to know
                    // which geometry it is.
                    var (producedChunks, producedHash) = await _chunkingService.ChunkAndStreamChangedAsync(
                        filePath, existingHashes, channel.Writer, cdcProgress, memoryBudget, largeChunkPool, smallChunkPool, cancellationToken);
                    return (producedChunks, producedHash);
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            // Track chunks uploaded for status messages. Padded - see
            // chunksUploadedCount declaration above for rationale.

            // Consumers: upload workers read from channel in parallel
            var consumerTasks = Enumerable.Range(0, maxParallelChunkUploads).Select(async _ =>
            {
                await foreach (var payload in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    // B30: producer-side charging means the budget was
                    // already debited when this payload's buffer was
                    // allocated. The consumer does NOT re-acquire here --
                    // doing so would double-charge each chunk and the
                    // budget would saturate at half capacity. We only
                    // need to mirror the producer's release in the
                    // finally block below.
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
                            var uploaded = bytesUploaded.Add(b);
                            progress?.Report((uploaded, totalFileSize));
                        });

                        // B30/B38: the producer charge in
                        // ChunkingService.AcquireChunkBufferAsync covers
                        // BOTH the chunk-payload buffer AND the
                        // downstream encrypt-side rented buffer in a
                        // single Acquire. The blob service does not
                        // re-acquire from the same budget here -- doing
                        // so would create a producer-vs-consumer
                        // circular wait, since the producer charge can
                        // only release after the consumer finishes the
                        // upload, but the consumer's encrypt-Acquire
                        // would block on the producer charge that is
                        // already filling the budget.
                        //
                        // B73 (W5 Phase 4 Commit 2): the encrypted scratch
                        // buffer that AzureBlobService.UploadChunk*Async
                        // rents is now routed through the operation-scope
                        // ChunkBufferPool instead of ArrayPool<byte>.Shared.
                        // Pool selection mirrors the producer-side partition
                        // in ChunkingService: encrypted size >=
                        // PoolSkipThresholdBytes -> large pool, else small.
                        // The small pool also covers buffers below its
                        // smallest bucket (it falls back to a fresh
                        // allocation, but Return is still a no-op for
                        // non-pool-shaped lengths) so the dispatch is
                        // safe at every chunk size.
                        //
                        // B74 (W5 Phase 4 Commit 3, Fix C2): revert B73's
                        // LARGE-pool encrypted-buffer routing. The
                        // ChunkBufferPool never evicts under GC pressure
                        // (by design -- see B37 rationale), while
                        // ArrayPool<byte>.Shared trims its per-core tier
                        // caches at every gen-2 collection via the BCL's
                        // Gen2GcCallback. For large-chunk workloads
                        // (LargeFileConfig, encrypted form >= 16 MB) the
                        // pre-B73 GC trim was doing real work, and B73's
                        // routing pinned those bytes at the high-water
                        // mark instead. Reverting the large branch to
                        // ArrayPool restores the steady-state decay the
                        // production media-library / large-file workloads
                        // depend on. The small-pool routing is kept
                        // because (a) the small-pool retention is
                        // bounded by the 12.5%-of-budget / 64 MB-floor
                        // cap so it cannot dominate residency, and (b)
                        // the B72 retention charge already attributes
                        // its cached bytes to the budget so it is
                        // strictly an improvement over ArrayPool's
                        // un-attributed per-core caches.
                        var encryptedSize = payload.Length + EncryptionService.EncryptionOverhead;
                        var encryptedBufferPool = encryptedSize >= ChunkingService.PoolSkipThresholdBytes
                            ? null
                            : smallChunkPool;
                        payload.Info.BlobName = isNewFile
                            ? await _blobService.UploadChunkDirectAsync(chunkData, payload.Info.Hash, encryptedBufferPool, storageTier,
                                uploadProgress, cancellationToken)
                            : await _blobService.UploadChunkAsync(chunkData, payload.Info.Hash, encryptedBufferPool, storageTier,
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

                        var completed = chunksUploadedCount.Increment();
                        StatusChanged?.Invoke(this, $"Uploading: {fileName} — chunk {completed} ({FormatHelper.FormatBytes(bytesUploaded.Read())}/{FormatHelper.FormatBytes(totalFileSize)})");
                    }
                    finally
                    {
                        diag.RecordChunk("ZeroMemory", payload.Info.Index, payload.Info.Hash,
                            payload.Length,
                            extra: $"firstByte=0x{(payload.Length > 0 ? payload.Data[0] : 0):X2}");
                        CryptographicOperations.ZeroMemory(payload.Data.AsSpan(0, payload.Length));

                        // B33 / B37 / B69 / B70: respect the producer's
                        // allocation decision. Pool ownership is carried
                        // on the payload itself so the consumer routes
                        // to exactly the pool that originally rented
                        // the buffer:
                        //   - BufferPool != null   -> owned recycler
                        //                            (small or large)
                        //   - ReturnToPool == true -> ArrayPool.Shared
                        //   - otherwise            -> GC reclaim
                        // Returning a non-pool array via ArrayPool.Return
                        // silently corrupts the pool's tier buckets, so
                        // the order above is load-bearing.
                        if (payload.BufferPool != null)
                        {
                            payload.BufferPool.Return(payload.Data);
                        }
                        else if (payload.ReturnToPool)
                        {
                            ArrayPool<byte>.Shared.Return(payload.Data);
                        }

                        // B30: release exactly what the producer charged.
                        // Using payload.ChargedBytes (not payload.Length)
                        // mirrors the producer's accounting decision so a
                        // pool-tier-rounded charge is fully reclaimed.
                        if (payload.ChargedBytes > 0)
                        {
                            memoryBudget?.Release(payload.ChargedBytes);
                        }
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
            var totalBytesUploaded = bytesUploaded.Read();
            var config = _databaseService.GetConfiguration();
            config.TotalBytesUploaded += totalBytesUploaded;
            config.LastBackupTime = DateTime.UtcNow;
            _databaseService.SaveConfiguration(config);

            // Report final progress
            progress?.Report((totalFileSize, totalFileSize));

            ProgressChanged?.Invoke(this, new BackupProgressEventArgs
            {
                FilePath = filePath,
                BytesUploaded = totalBytesUploaded,
                ChunksUploaded = chunksToUpload.Count,
                TotalChunks = chunks.Count
            });

            StatusChanged?.Invoke(this, $"Completed: {fileName} — {chunksToUpload.Count}/{chunks.Count} chunks uploaded ({FormatHelper.FormatBytes(totalBytesUploaded)})");

            // Record per-file throughput metrics
            fileStopwatch.Stop();
            var fileElapsed = fileStopwatch.Elapsed.TotalSeconds;
            Metrics?.RecordFile(new FileMetrics
            {
                Operation = "backup",
                Path = filePath,
                Bytes = totalFileSize,
                Chunks = chunks.Count,
                ChunkMin = chunks.Min(c => c.Length),
                ChunkMax = chunks.Max(c => c.Length),
                ElapsedSeconds = fileElapsed,
                ThroughputMBps = fileElapsed > 0 ? totalFileSize / fileElapsed / (1024 * 1024) : 0,
                NewChunks = chunksToUpload.Count,
                DedupChunks = chunks.Count - chunksToUpload.Count,
                Tier = storageTier.ToString()
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            // B23: cancellation is a user-initiated outcome, NOT an error
            // worth surfacing as a .diag file. Pre-B23 a Cancel button
            // press during a 1000-file backup produced a per-file .diag
            // for every in-flight file (8 parallel workers => 8 stray
            // diag files at minimum), drowning the user's "find the
            // real failures" workflow in noise. Discarding here also
            // removes the diag from the live registry so the shutdown
            // hook does NOT write a stale snapshot at app exit.
            diag.Discard();
            throw;
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
        finally
        {
            // B23: belt-and-braces -- on the happy path the try-block
            // returned true above without throwing, so we land here with
            // diag still in the live registry. Discard removes it so
            // the shutdown hook does not flush a stale snapshot to disk
            // for a file that backed up cleanly. Idempotent: calling
            // Discard after Flush (the catch path) is a no-op because
            // both routes through the same _isFlushed Interlocked guard.
            diag.Discard();
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

                    // B54: clamp file-level fan-out against the per-batch
                    // budget so a small MemoryLimitMB does not over-subscribe
                    // in-flight residency in the background loop either.
                    var batchFileConcurrency =
                        ComputeEffectiveFileConcurrency(batchBudget, EffectiveMaxParallelFileBackups);

                    Log($"RunBackupLoopAsync: Processing {backups.Count} files in parallel " +
                        $"(max {batchFileConcurrency}, " +
                        $"memoryBudget={(!batchBudget.IsUnlimited ? $"{batchConfig.MemoryLimitMB} MB" : "unlimited")})");

                    await Parallel.ForEachAsync(
                        backups,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = batchFileConcurrency,
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
