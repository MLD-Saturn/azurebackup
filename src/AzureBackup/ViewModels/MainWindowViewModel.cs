using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    // Services
    private readonly LocalDatabaseService _databaseService;
    private readonly EncryptionService _encryptionService;
    private readonly ChunkingService _chunkingService;
    private readonly IBlobStorageService _blobService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly BackupOrchestrator _orchestrator;
    private readonly RestoreService _restoreService;
    private readonly ThroughputMetrics _throughputMetrics;
    private ChunkIndexService? _chunkIndexService;

    /// <summary>
    /// Periodic LiteDB WAL checkpoint timer (Phase 5 / discovered-#3). LiteDB only
    /// checkpoints automatically when the WAL crosses its internal threshold or on
    /// a clean shutdown; on long-running sessions with sustained small writes the
    /// <c>-log</c> file can grow into the gigabytes before either trigger fires.
    /// An hourly explicit checkpoint flushes the WAL into the main data file so
    /// the next open stays fast and disk usage stays predictable.
    /// </summary>
    private System.Threading.Timer? _checkpointTimer;

    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// Creates a new CancellationTokenSource for an operation, disposing the previous one if it exists.
    /// </summary>
    private CancellationTokenSource CreateOperationCts()
    {
        // Dispose previous CTS if it exists
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        
        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    /// <summary>
    /// ViewModel for the Storage Health tab.
    /// </summary>
    public StorageHealthViewModel? StorageHealthViewModel { get; private set; }

    /// <summary>
    /// ViewModel for the Tier Migration tab.
    /// </summary>
    public TierMigrationViewModel? TierMigrationViewModel { get; private set; }

    /// <summary>
    /// ViewModel for the Data Integrity tab (D2). Created lazily on first
    /// "Check Data Integrity" click from the Storage Health tab so the
    /// nav button (and its subtree) does not exist until the user opts in.
    /// Once shown the tab persists for the rest of the app session per the
    /// design discussion.
    /// </summary>
    [ObservableProperty]
    private DataIntegrityViewModel? _dataIntegrityTabVm;

    /// <summary>
    /// Drives the visibility of the "Data Integrity" nav button.
    /// True once the user has opened the tab at least once.
    /// </summary>
    [ObservableProperty]
    private bool _showDataIntegrityNavButton;

    private IntegrityCheckService? _integrityService;

    /// <summary>
    /// I6: background drainer for upload-time MD5 stamping. Created in
    /// the auth flow once <see cref="_databaseService"/> is initialized;
    /// disposed in <see cref="DisposeAsync"/> so any buffered MD5s flush.
    /// </summary>
    private ExpectedMd5Drain? _expectedMd5Drain;

    /// <summary>
    /// Window title including mode indicator (Portable or Installed).
    /// </summary>
    public string WindowTitle => $"Azure Backup - Encrypted Cloud Backup{AppMode.WindowTitleSuffix}";

    /// <summary>
    /// Gets whether the app is running in portable mode.
    /// </summary>
    public bool IsPortableMode => AppMode.IsPortable;

    /// <summary>
    /// Gets the data directory path for display in settings.
    /// </summary>
    public string DataDirectoryPath => AppMode.DataDirectory;

    // Observable properties
    [ObservableProperty]
    private string _statusMessage = "Not connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupStatusText))]
    [NotifyPropertyChangedFor(nameof(CanUpdateConnectionString))]
    private bool _isInitialized;



    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupStatusText))]
    private bool _isBackupRunning;

    /// <summary>
    /// Indicates if this is a returning user with existing configuration.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewUser))]
    [NotifyPropertyChangedFor(nameof(PasswordSectionTitle))]
    [NotifyPropertyChangedFor(nameof(InitializeButtonText))]
    [NotifyPropertyChangedFor(nameof(UnlockAndConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(BackupStatusText))]
    private bool _hasExistingConfig;

    /// <summary>
    /// True if this is a new user (no existing password/config).
    /// </summary>
    public bool IsNewUser => !HasExistingConfig;

    /// <summary>
    /// Dynamic title for password section based on user state.
    /// </summary>
    public string PasswordSectionTitle => HasExistingConfig 
        ? "Unlock (Enter Your Password)" 
        : "Set Encryption Password";

    /// <summary>
    /// Text for the initialize/unlock button.
    /// </summary>
    public string InitializeButtonText => HasExistingConfig ? "Unlock" : "Initialize";

    /// <summary>
    /// Text for the unified unlock and connect button.
    /// </summary>
    public string UnlockAndConnectButtonText => HasExistingConfig 
        ? "🔓 Unlock" 
        : "🔓 Initialize & Connect";

    /// <summary>
    /// Human-readable backup status text.
    /// </summary>
    public string BackupStatusText => IsBackupRunning 
        ? "Monitoring for Changes" 
        : IsInitialized 
            ? "Ready (Not Monitoring)" 
            : HasExistingConfig
                ? "Locked (Enter Password)"
                : "Not Configured";

    /// <summary>
    /// True if the application needs initial configuration (no Azure storage configured).
    /// </summary>
    public bool NeedsConfiguration => !HasExistingConfig;

    /// <summary>
    /// True if the application is configured but locked (needs password).
    /// Includes migration case - migration is handled automatically during unlock.
    /// </summary>
    public bool NeedsUnlock => HasExistingConfig && !IsInitialized;

    /// <summary>
    /// True if migration from unencrypted database is required.
    /// </summary>
    public bool NeedsMigration => _needsMigration;

    // Flag to track if migration from legacy encrypted database is needed (raw password, no Argon2id)
    private bool _needsLegacyMigration;

    [ObservableProperty]
    private bool _isOperationInProgress;

    /// <summary>
    /// True when a file transfer operation (backup, restore, sync) is in progress.
    /// This shows the detailed progress panel with progress bars.
    /// Distinguished from IsOperationInProgress which includes simple refreshes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltipText))]
    private bool _isTransferInProgress;

    /// <summary>
    /// True when the Progress nav button should be visible in the navigation bar.
    /// Visible while an operation is active or when the completion summary is showing.
    /// </summary>
    [ObservableProperty]
    private bool _showProgressNavButton;

    /// <summary>
    /// ViewModel for the Progress tab that appears during backup/restore/mirror operations.
    /// </summary>
    public ProgressTabViewModel ProgressTab { get; }

    // Stores the view the user was on before the Progress tab auto-switched
    private string _viewBeforeProgress = "Sync";

    /// <summary>
    /// True when a reset has been requested and is awaiting confirmation.
    /// </summary>
    [ObservableProperty]
    private bool _isResetPending;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordMismatch))]
    private string _password = string.Empty;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordMismatch))]
    private string _passwordConfirm = string.Empty;


    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    private bool _showConnectionString;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateConnectionString))]
    private bool _isEditingConnectionString;

    /// <summary>
    /// True when the user can click "Update Connection String" — initialized and not already editing.
    /// </summary>
    public bool CanUpdateConnectionString => IsInitialized && !IsEditingConnectionString;

    /// <summary>
    /// Returns true if passwords don't match (and both have values).
    /// </summary>
    public bool PasswordMismatch => !string.IsNullOrEmpty(Password) && 
                                    !string.IsNullOrEmpty(PasswordConfirm) && 
                                    Password != PasswordConfirm;

    // Authentication method selection
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseEntraId))]
    [NotifyPropertyChangedFor(nameof(UseConnectionString))]
    private bool _useEntraIdAuth = false;
    
    /// <summary>
    /// True when using Entra ID authentication (for work/school accounts).
    /// </summary>
    public bool UseEntraId => UseEntraIdAuth;
    
    /// <summary>
    /// True when using Connection String authentication (for personal accounts).
    /// </summary>
    public bool UseConnectionString => !UseEntraIdAuth;

    // Entra ID authentication state
    [ObservableProperty]
    private bool _isEntraIdAuthenticated;

    [ObservableProperty]
    private string _entraIdStatus = "Not signed in";

    // Connection String authentication
    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private string _storageAccountName = string.Empty;

    [ObservableProperty]
    private string _containerName = "backup";

    // Memory limit settings
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryLimitStatusText))]
    [NotifyPropertyChangedFor(nameof(MemoryLimitColor))]
    [NotifyPropertyChangedFor(nameof(MemoryLimitDisplayText))]
    private bool _memoryLimitEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryLimitStatusText))]
    [NotifyPropertyChangedFor(nameof(MemoryLimitColor))]
    [NotifyPropertyChangedFor(nameof(MemoryLimitDisplayText))]
    private int _memoryLimitSliderIndex;

    /// <summary>
    /// Stepped detent values (MB) for the memory limit slider.
    /// Computed from total physical RAM at startup.
    /// </summary>
    public int[] MemoryLimitSteps { get; } = SystemMemoryHelper.GetMemorySteps(
        SystemMemoryHelper.GetTotalPhysicalMemoryBytes());

    /// <summary>
    /// Total physical RAM in bytes, detected at startup.
    /// </summary>
    public long TotalPhysicalMemoryBytes { get; } = SystemMemoryHelper.GetTotalPhysicalMemoryBytes();

    /// <summary>
    /// The memory limit in MB corresponding to the current slider index.
    /// </summary>
    public int MemoryLimitMB => MemoryLimitSteps.Length > 0 && MemoryLimitSliderIndex >= 0 && MemoryLimitSliderIndex < MemoryLimitSteps.Length
        ? MemoryLimitSteps[MemoryLimitSliderIndex]
        : 2048;

    /// <summary>
    /// Display text for the selected memory amount (e.g., "4 GB" or "512 MB").
    /// </summary>
    public string MemoryLimitDisplayText => FormatHelper.FormatBytes((long)MemoryLimitMB * 1024 * 1024);

    /// <summary>
    /// Multi-line status text showing selected amount, total, and estimated available.
    /// </summary>
    public string MemoryLimitStatusText
    {
        get
        {
            var estimated = SystemMemoryHelper.GetEstimatedAvailableMemoryBytes();
            return $"Selected: {FormatHelper.FormatBytes((long)MemoryLimitMB * 1024 * 1024)}\n" +
                   $"Total System Memory: {FormatHelper.FormatBytes(TotalPhysicalMemoryBytes)}\n" +
                   $"Estimated Memory Available: {FormatHelper.FormatBytes(estimated)}";
        }
    }

    /// <summary>
    /// Color name for the memory limit indicator (Green, Orange, or Red).
    /// </summary>
    public string MemoryLimitColor => SystemMemoryHelper.GetSeverity(MemoryLimitMB, TotalPhysicalMemoryBytes) switch
    {
        MemoryLimitSeverity.Safe => "LimeGreen",
        MemoryLimitSeverity.Aggressive => "Orange",
        MemoryLimitSeverity.Dangerous => "Red",
        _ => "LimeGreen"
    };

    [ObservableProperty]
    private string _currentView = "Sync";

    // Statistics
    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private string _totalSize = "0 B";

    [ObservableProperty]
    private int _pendingChanges;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    // Detailed progress tracking
    [ObservableProperty]
    private string _currentOperationType = string.Empty;

    [ObservableProperty]
    private int _completedFilesCount;

    [ObservableProperty]
    private int _totalFilesInOperation;

    [ObservableProperty]
    private long _totalBytesProcessed;

    [ObservableProperty]
    private long _totalBytesToProcess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltipText))]
    private string _operationSpeed = string.Empty;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltipText))]
    private string _estimatedTimeRemaining = string.Empty;

    /// <summary>
    /// Dynamic tooltip text for the system tray icon.
    /// Shows "Azure Backup — Idle" or "Azure Backup — Syncing — speed — ETA".
    /// </summary>
    public string TrayTooltipText
    {
        get
        {
            if (!IsTransferInProgress)
                return "Azure Backup \u2014 Idle";

            var text = "Azure Backup \u2014 Syncing";
            if (!string.IsNullOrEmpty(OperationSpeed))
                text += $" \u2014 {OperationSpeed}";
            if (!string.IsNullOrEmpty(EstimatedTimeRemaining))
                text += $" \u2014 {EstimatedTimeRemaining}";
            return text;
        }
    }

    // Track current file index for display (0-based internally, shown as 1-based)
    private int _currentFileIndex;

    /// <summary>
    /// Formatted string showing bytes processed vs total.
    /// </summary>
    public string BytesProgressText => TotalBytesToProcess > 0
        ? $"{AzureBackup.Core.FormatHelper.FormatBytes(TotalBytesProcessed)} / {AzureBackup.Core.FormatHelper.FormatBytes(TotalBytesToProcess)}"
        : string.Empty;

    /// <summary>
    /// Formatted string showing files processed vs total.
    /// Shows "X of Y files" where X is the current file being processed (1-based).
    /// </summary>
    public string FilesProgressText => TotalFilesInOperation > 0
        ? $"{Math.Max(CompletedFilesCount, _currentFileIndex + 1)} of {TotalFilesInOperation} files"
        : string.Empty;

    // Collections
    public ObservableCollection<WatchedFolderViewModel> WatchedFolders { get; } = [];
    public BulkObservableCollection<BackedUpFileViewModel> BackedUpFiles { get; } = [];
    public BulkObservableCollection<BackedUpFileViewModel> RestorableFiles { get; } = [];
    public BulkObservableCollection<string> LogMessages { get; } = [];

    // Buffered log queue — ensures messages appear in chronological order regardless
    // of which thread calls AddLog. Without this, concurrent Dispatcher.Post calls
    // can interleave and insert messages out of timestamp order.
    private readonly ConcurrentQueue<string> _pendingLogMessages = new();
    private int _logDrainScheduled;
    private string? _latestStatusMessage;

    /// <summary>
    /// True if no restorable files have been loaded.
    /// </summary>
    public bool RestorableFilesEmpty => RestorableFiles.Count == 0;

    /// <summary>
    /// True if the Azure files panel should show the empty state.
    /// Shows empty state when there are no files AND we're not in tree view with tree nodes.
    /// </summary>
    public bool ShowAzureEmptyState => RestorableFiles.Count == 0 && FileTreeRoots.Count == 0;

    /// <summary>
    /// Display text showing count of restorable files.
    /// </summary>
    public string RestorableFilesCount => RestorableFiles.Count == 0 
        ? "" 
        : $"{RestorableFiles.Count} file(s)";


    /// <summary>
    /// Gets the files that are currently selected (checked) for restore operations.
    /// </summary>
    public IEnumerable<BackedUpFileViewModel> SelectedRestoreFiles => 
        RestorableFiles.Where(f => f.IsSelected);

    /// <summary>
    /// Count of selected files for display.
    /// </summary>
    public int SelectedFilesCount => UseTreeView 
        ? FileTreeRoots.Sum(r => r.GetSelectedFiles().Count())
        : RestorableFiles.Count(f => f.IsSelected);

    /// <summary>
    /// True if any files are selected.
    /// </summary>
    public bool HasSelectedFiles => UseTreeView
        ? FileTreeRoots.Any(r => r.GetSelectedFiles().Any())
        : RestorableFiles.Any(f => f.IsSelected);

    /// <summary>
    /// Display text for selected files count.
    /// </summary>
    public string SelectedFilesText => SelectedFilesCount == 0 
        ? "" 
        : $"{SelectedFilesCount} selected";

    // Selected items
    [ObservableProperty]
    private WatchedFolderViewModel? _selectedWatchedFolder;

    /// <summary>
    /// The last clicked file for shift-click range selection.
    /// Not displayed in UI — used only for internal range selection logic.
    /// </summary>
    private BackedUpFileViewModel? _lastClickedFile;

    [ObservableProperty]
    private BackedUpFileViewModel? _selectedRestoreFile;

    [ObservableProperty]
    private string _restoreDirectory = string.Empty;

    /// <summary>
    /// When true, files are restored to their original paths instead of a custom directory.
    /// </summary>
    [ObservableProperty]
    private bool _restoreToOriginalLocation = true;

    [ObservableProperty]
    private string _searchPattern = string.Empty;
    
    /// <summary>
    /// Controls whether diagnostic logging is enabled (shows detailed service logs).
    /// </summary>
    [ObservableProperty]
    private bool _enableDiagnosticLogging = true;

    /// <summary>
    /// Font size for log entries. Adjustable via Ctrl+= / Ctrl+- on the Logs tab.
    /// Persists across tab navigation since the ViewModel outlives the view.
    /// </summary>
    [ObservableProperty]
    private double _logFontSize = 12;

    /// <summary>
    /// Uniform scale factor for the Settings page (1.0 = 100%).
    /// Adjustable via +/- buttons on the Settings tab.
    /// Clamped to [0.8, 2.0] to keep the page usable.
    /// </summary>
    [ObservableProperty]
    private double _settingsScale = 1.0;

    [RelayCommand]
    private void IncreaseSettingsScale()
    {
        SettingsScale = Math.Min(SettingsScale + 0.1, 2.0);
    }

    [RelayCommand]
    private void DecreaseSettingsScale()
    {
        SettingsScale = Math.Max(SettingsScale - 0.1, 0.8);
    }

    [RelayCommand]
    private void ResetSettingsScale()
    {
        SettingsScale = 1.0;
    }

    #region Tree View Properties

    /// <summary>
    /// Root nodes for the file tree view (Azure backup files).
    /// </summary>
    public BulkObservableCollection<FileTreeNodeViewModel> FileTreeRoots { get; } = [];

    /// <summary>
    /// Root nodes for the local file tree view.
    /// </summary>
    public BulkObservableCollection<LocalFileTreeNodeViewModel> LocalFileTreeRoots { get; } = [];

    /// <summary>
    /// Flat list of all local files for list view mode.
    /// </summary>
    public BulkObservableCollection<LocalFileTreeNodeViewModel> LocalFilesFlatList { get; } = [];


    /// <summary>
    /// Whether to show tree view (true) or flat list (false).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFlatList))]
    private bool _useTreeView = true;

    /// <summary>
    /// Inverse of UseTreeView for binding convenience.
    /// </summary>
    public bool ShowFlatList => !UseTreeView;

    /// <summary>
    /// Currently selected tree node (for path remapping).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetCustomRestorePath))]
    [NotifyPropertyChangedFor(nameof(CanDeleteFromAzure))]
    private FileTreeNodeViewModel? _selectedTreeNode;

    /// <summary>
    /// Currently selected local file tree node.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelectedLocalFolder))]
    private LocalFileTreeNodeViewModel? _selectedLocalTreeNode;

    /// <summary>
    /// True if deletion from Azure is possible (either selected tree node or checked files exist).
    /// </summary>
    public bool CanDeleteFromAzure => HasSelectedFiles || SelectedTreeNode != null;




    /// <summary>
    /// True if a custom restore path can be set (a folder is selected).
    /// </summary>
    public bool CanSetCustomRestorePath => SelectedTreeNode?.IsFolder == true;

    /// <summary>
    /// Custom restore base path for the selected folder.
    /// </summary>
    [ObservableProperty]
    private string _customRestoreBasePath = string.Empty;

    /// <summary>
    /// Search pattern for filtering files in the Sync view.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredLocalFiles))]
    [NotifyPropertyChangedFor(nameof(FilteredRestorableFiles))]
    private string _syncSearchPattern = string.Empty;

    /// <summary>
    /// Filtered local files based on search pattern.
    /// </summary>
    public IEnumerable<LocalFileTreeNodeViewModel> FilteredLocalFiles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SyncSearchPattern))
                return LocalFilesFlatList;
            
            return LocalFilesFlatList.Where(f => 
                f.FullPath.Contains(SyncSearchPattern, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains(SyncSearchPattern, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Filtered restorable files based on search pattern.
    /// </summary>
    public IEnumerable<BackedUpFileViewModel> FilteredRestorableFiles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SyncSearchPattern))
                return RestorableFiles;
            
            return RestorableFiles.Where(f => 
                f.LocalPath.Contains(SyncSearchPattern, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Contains(SyncSearchPattern, StringComparison.OrdinalIgnoreCase));
        }
    }

    #endregion

    #region Sync Progress Properties

    /// <summary>
    /// Summary statistics for local files.
    /// </summary>
    public string LocalFilesSummary
    {
        get
        {
            var totalFiles = LocalFileTreeRoots.Sum(r => r.TotalFileCount);
            var newCount = LocalFileTreeRoots.Sum(r => r.NewCount);
            var modifiedCount = LocalFileTreeRoots.Sum(r => r.ModifiedCount);
            var backedUpCount = LocalFileTreeRoots.Sum(r => r.BackedUpCount);
            
            if (totalFiles == 0) return "No files in watched folders";
            
            System.Collections.Generic.List<string> parts = new() { $"{totalFiles} total" };
            if (newCount > 0) parts.Add($"{newCount} new");
            if (modifiedCount > 0) parts.Add($"{modifiedCount} modified");
            if (backedUpCount > 0) parts.Add($"{backedUpCount} backed up");
            
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Summary statistics for Azure backup files.
    /// </summary>
    public string AzureFilesSummary
    {
        get
        {
            var count = RestorableFiles.Count;
            if (count == 0) return "No files in Azure backup";
            var totalSize = RestorableFiles.Sum(f => f.Model.FileSize);
            return $"{count} files ({AzureBackup.Core.FormatHelper.FormatBytes(totalSize)})";
        }
    }

    #endregion

    #region Drag-Drop Visual State

    /// <summary>
    /// True when files are being dragged over the Azure panel (for backup).
    /// </summary>
    [ObservableProperty]
    private bool _isDragOverAzurePanel;

    /// <summary>
    /// True when files are being dragged over the Local panel (for restore).
    /// </summary>
    [ObservableProperty]
    private bool _isDragOverLocalPanel;

    /// <summary>
    /// True when a drag operation is in progress.
    /// </summary>
    public bool IsDragging => IsDragOverAzurePanel || IsDragOverLocalPanel;

    #endregion

    public MainWindowViewModel()
    {
        // Initialize services (database is NOT initialized yet - needs password)
        _databaseService = new LocalDatabaseService();
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        var blobService = new AzureBlobService(_encryptionService);
        _blobService = blobService;
        _fileWatcherService = new FileWatcherService(_databaseService);
        _orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, _chunkingService, 
            _blobService, _fileWatcherService);
        _restoreService = new RestoreService(_databaseService, _blobService, _encryptionService);

        // Wire per-file diagnostics directory so .diag logs land next to the database
        var diagDir = Path.Combine(AppMode.DataDirectory, "diagnostics");
        _orchestrator.DiagnosticsDirectory = diagDir;
        _restoreService.DiagnosticsDirectory = diagDir;

        // Initialize throughput metrics logger for performance analysis
        var metricsDir = Path.Combine(AppMode.DataDirectory, "metrics");
        var throughputMetrics = new ThroughputMetrics(metricsDir);
        _throughputMetrics = throughputMetrics;
        _orchestrator.Metrics = throughputMetrics;
        _restoreService.Metrics = throughputMetrics;
        throughputMetrics.CleanupOldFiles();

        // Initialize progress tab for backup/restore/mirror operations
        ProgressTab = new ProgressTabViewModel();
        ProgressTab.CompletionAcknowledged += (_, _) =>
        {
            ShowProgressNavButton = false;
            CurrentView = _viewBeforeProgress;
        };
        ProgressTab.CancelRequested += (_, _) => _operationCts?.Cancel();

        // Initialize chunk index service
        _chunkIndexService = new ChunkIndexService(_databaseService, _blobService, _encryptionService);
        _orchestrator.SetChunkIndexService(_chunkIndexService);
        
        // Initialize Storage Health ViewModel
        StorageHealthViewModel = new StorageHealthViewModel(_chunkIndexService, _databaseService);
        StorageHealthViewModel.OpenDataIntegrityRequested += (_, _) => OpenDataIntegrityTab();

        // Initialize the integrity-check engine -- shared across runs of the
        // tab. Wire the diagnostics directory + session id so per-file
        // .diag files land in the same place the X4 bundle picks up, and
        // so the run row carries a SessionId that correlates with logs.
        _integrityService = new IntegrityCheckService(_databaseService, _blobService, _encryptionService)
        {
            DiagnosticsDirectory = System.IO.Path.Combine(AppMode.DataDirectory, "diagnostics"),
            SessionId = Program.Logger?.SessionId ?? Guid.Empty
        };
        if (Program.Logger != null)
        {
            _integrityService.DiagnosticLog += (_, msg) => Program.Logger.Log(msg);
        }

        // D6 + I6: wire the upload-time MD5 capture through a background
        // drain so a slow SetChunkExpectedMd5 (large WAL flush, contended
        // write lock) does not back-pressure the upload pipeline. The
        // callback enqueues in O(1) and returns immediately; a single
        // reader task drains the queue and persists each MD5. The drain
        // is disposed in DisposeAsync so any buffered MD5s flush during
        // app shutdown.
        _expectedMd5Drain = new ExpectedMd5Drain(_databaseService);
        _blobService.OnChunkUploaded = (chunkHash, md5) => _expectedMd5Drain.Enqueue(chunkHash, md5);

        // Initialize Tier Migration ViewModel
        TierMigrationViewModel = new TierMigrationViewModel(_blobService, _chunkIndexService, msg => AddLog(msg));

        // Wire up status events
        _orchestrator.StatusChanged += (s, msg) => AddLog(msg);
        _orchestrator.ErrorOccurred += (s, msg) => AddLog($"ERROR: {msg}");
        _orchestrator.ProgressChanged += (s, e) => UpdateProgress(e);
        // When Azure rejects our credential, reset the authenticated UI state so the
        // user is prompted to sign in again instead of watching silent retry stalls.
        _orchestrator.AuthenticationFailed += (s, ex) =>
        {
            AddLog($"Azure authentication failed (HTTP {ex.Status}). Please sign in again from Settings.");
            IsEntraIdAuthenticated = false;
            EntraIdStatus = "Not signed in";
        };

        _restoreService.StatusChanged += (s, msg) => AddLog(msg);
        _restoreService.ErrorOccurred += (s, msg) => AddLog($"ERROR: {msg}");
        
        // Wire up file system change events for auto-refresh
        _fileWatcherService.FileChanged += (s, e) => OnFileSystemChanged();
        
        // Wire up local file selection change events
        LocalFileTreeNodeViewModel.SelectionChanged += OnLocalFileSelectionChanged;

        // Wire up Azure file tree selection change events
        FileTreeNodeViewModel.SelectionChanged += OnAzureFileSelectionChanged;
        
        // Wire up diagnostic logging events (detailed service logs)
        _orchestrator.DiagnosticLog += OnDiagnosticLog;
        blobService.DiagnosticLog += OnDiagnosticLog;
        _databaseService.DiagnosticLog += OnDiagnosticLog;
        _encryptionService.DiagnosticLog += OnDiagnosticLog;
        _restoreService.DiagnosticLog += OnDiagnosticLog;
        _fileWatcherService.DiagnosticLog += OnDiagnosticLog;

        // Wire up crash-safe file logger for all service diagnostic events
        if (Program.Logger != null)
        {
            _orchestrator.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            blobService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _databaseService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _encryptionService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _restoreService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _fileWatcherService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            
            _restoreService.ErrorOccurred += (s, msg) => Program.Logger.Log($"[ERROR] {msg}");
            _orchestrator.ErrorOccurred += (s, msg) => Program.Logger.Log($"[ERROR] {msg}");
            
            Program.Logger.Log($"Services initialized, log file: {Program.Logger.LogFilePath}");
            // Echo the SessionId into the UI log too so a tester capturing
            // a screenshot of the Logs tab can quote it back without
            // hunting through the data directory for the file log.
            AddLog($"Session: {Program.Logger.SessionId:N}");
        }

        AddLog("Application started - diagnostic logging enabled");

        // Check database state for returning vs new user
        var dbPath = AppMode.DatabasePath;

        // Crash-safety: if a previous launch died mid-encryption-upgrade,
        // finish the rename chain before we probe for database existence
        // or migration state. The recovery is a no-op when no sentinel
        // file is present, so this is cheap on the happy path.
        try
        {
            if (LocalDatabaseService.RecoverInterruptedUpgrade(dbPath))
            {
                AddLog("Recovered an interrupted encryption upgrade from a prior session.");
            }
        }
        catch (System.Exception ex)
        {
            AddLog($"Could not recover prior upgrade state: {ex.Message}. " +
                "Inspect the database directory before continuing.");
        }

        HasExistingConfig = LocalDatabaseService.DatabaseExists(dbPath);
        
        // Check what type of migration is needed (if any)
        _needsMigration = HasExistingConfig && LocalDatabaseService.IsUnencryptedDatabase(dbPath);
        _needsLegacyMigration = HasExistingConfig && LocalDatabaseService.IsLegacyEncryptedDatabase(dbPath);
        
        if (_needsMigration)
        {
            AddLog("Legacy unencrypted database detected - will migrate to encrypted format");
        }
        else if (_needsLegacyMigration)
        {
            AddLog("Legacy encrypted database detected - will upgrade to stronger Argon2id encryption");
        }
        else if (HasExistingConfig)
        {
            AddLog("Encrypted database found - enter password to unlock");
        }
        else
        {
            AddLog("No existing configuration - set up a new password to get started");
        }
    }
    
    // Flag to track if migration from unencrypted database is needed
    private bool _needsMigration;

    /// <summary>
    /// Saves memory limit settings to the database when changed.
    /// </summary>
    private void SaveMemoryLimitSettings()
    {
        if (!_databaseService.IsInitialized)
            return;

        var config = _databaseService.GetConfiguration();
        config.MemoryLimitEnabled = MemoryLimitEnabled;
        config.MemoryLimitMB = MemoryLimitMB;
        _databaseService.SaveConfiguration(config);
    }

    partial void OnMemoryLimitEnabledChanged(bool value)
    {
        SaveMemoryLimitSettings();
    }

    partial void OnMemoryLimitSliderIndexChanged(int value)
    {
        SaveMemoryLimitSettings();
    }

    /// <summary>
    /// Handles diagnostic log messages from services.
    /// </summary>
    private void OnDiagnosticLog(object? sender, string message)
    {
        if (EnableDiagnosticLogging)
        {
            _pendingLogMessages.Enqueue($"\U0001f50d {message}");
            DrainLogQueue();
        }
    }

    /// <summary>
    /// Handles local file selection changes (checkbox state changes).
    /// </summary>
    private void OnLocalFileSelectionChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(NotifyLocalSelectionChanged);
    }

    /// <summary>
    /// Handles Azure file tree selection changes (checkbox state changes).
    /// </summary>
    private void OnAzureFileSelectionChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(NotifySelectionChanged);
    }

    private void LoadConfiguration()
    {
        // Database must be initialized before loading configuration
        if (!_databaseService.IsInitialized)
        {
            AddLog("Database not yet initialized - waiting for password");
            return;
        }
        
        AddLog("Loading configuration...");
        var config = _databaseService.GetConfiguration();
        
        // Load authentication method
        UseEntraIdAuth = config.AuthMethod == AzureBackup.Core.Models.AzureAuthMethod.EntraId;
        
        // Load Entra ID settings
        StorageAccountName = config.StorageAccountName ?? string.Empty;
        IsEntraIdAuthenticated = config.IsEntraIdAuthenticated;
        EntraIdStatus = config.IsEntraIdAuthenticated 
            ? $"Signed in as {config.EntraIdUserName}" 
            : "Not signed in";
        
        // Load connection string indicator (actual value is encrypted)
        if (config.EncryptedConnectionString != null)
        {
            ConnectionString = "[Encrypted - stored securely]";
        }
        else
        {
            ConnectionString = string.Empty;
        }
        
        ContainerName = config.ContainerName ?? "backup";

        // Load memory limit settings
        MemoryLimitEnabled = config.MemoryLimitEnabled;
        var targetMB = config.MemoryLimitMB;
        var steps = MemoryLimitSteps;
        // Find the closest step index for the stored value
        var bestIndex = 0;
        for (var i = 0; i < steps.Length; i++)
        {
            if (Math.Abs(steps[i] - targetMB) < Math.Abs(steps[bestIndex] - targetMB))
                bestIndex = i;
        }
        MemoryLimitSliderIndex = bestIndex;

        // Update WatchedFolders collection directly
        // LoadConfiguration is called from UI thread (auth commands), so direct update is safe
        var folders = config.WatchedFolders;
        WatchedFolders.Clear();
        foreach (var folder in folders)
        {
            WatchedFolders.Add(new WatchedFolderViewModel(folder));
        }
        OnPropertyChanged(nameof(FilteredLocalFiles));
        OnPropertyChanged(nameof(LocalFilesSummary));

        // Session-context line: log the runtime config that drives perf
        // decisions (memory budget + diagnostic-log toggle + watched folder
        // count). Captured once per LoadConfiguration so a multi-hour test
        // session can attribute regressions to a config change.
        Program.Logger?.Log(
            $"Config loaded: MemoryLimitEnabled={MemoryLimitEnabled}, MemoryLimitMB={config.MemoryLimitMB}, " +
            $"DiagLogging={EnableDiagnosticLogging}, WatchedFolders={folders.Count}, " +
            $"WatchedFolderRoots=[{string.Join(", ", folders.Select(f => f.Path))}]");

        RefreshStatistics();
    }

    private void RefreshStatistics()
    {
        // Skip if database not initialized
        if (!_databaseService.IsInitialized)
            return;
            
        // Clean up any stale pending changes before getting stats
        _databaseService.CleanupStalePendingChanges();
        
        var stats = _databaseService.GetStatistics();
        TotalFiles = stats.TotalFiles;
        TotalSize = stats.TotalSizeFormatted;
        PendingChanges = stats.PendingChanges;
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _pendingLogMessages.Enqueue($"[{timestamp}] {message}");
        Interlocked.Exchange(ref _latestStatusMessage, message);
        DrainLogQueue();
    }

    /// <summary>
    /// Schedules a single UI-thread drain of the buffered log queue.
    /// Multiple concurrent AddLog calls coalesce into one drain, preserving enqueue order.
    /// </summary>
    private void DrainLogQueue()
    {
        // Only one Post in flight at a time — subsequent calls are no-ops
        // because the scheduled drain will pick up their enqueued messages too.
        if (Interlocked.CompareExchange(ref _logDrainScheduled, 1, 0) != 0)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _logDrainScheduled, 0);

            // Drain into a local list so we can compute the prepend + trim
            // off-collection. Insert(0, x) per item is O(N) on the underlying
            // List<T>, so a 200-message drain into a 1000-line buffer was
            // ~200 * (1000 + Add+Layout) before this change. A bulk Reset is
            // one notification regardless of size.
            List<string>? drained = null;
            while (_pendingLogMessages.TryDequeue(out var entry))
            {
                drained ??= new List<string>();
                drained.Add(entry);
            }

            if (drained != null && drained.Count > 0)
            {
                const int Cap = 1000;

                // Newest at top: drained is in enqueue order (oldest first),
                // so reverse it before prepending.
                var newCount = drained.Count + LogMessages.Count;
                if (newCount <= Cap)
                {
                    // Build a fresh list: newest drained items first (reverse
                    // order), then existing items, then trim to cap.
                    var rebuilt = new List<string>(newCount);
                    for (var i = drained.Count - 1; i >= 0; i--)
                        rebuilt.Add(drained[i]);
                    foreach (var existing in LogMessages)
                        rebuilt.Add(existing);
                    LogMessages.ReplaceAll(rebuilt);
                }
                else
                {
                    // Over cap: keep at most Cap items. If the drain itself
                    // exceeds Cap (~rare; would mean >1000 messages between
                    // dispatcher pumps) we take only the most recent Cap.
                    var rebuilt = new List<string>(Cap);
                    var fromDrained = System.Math.Min(drained.Count, Cap);
                    for (var i = drained.Count - 1; i >= drained.Count - fromDrained; i--)
                        rebuilt.Add(drained[i]);
                    if (fromDrained < Cap)
                    {
                        var fromExisting = Cap - fromDrained;
                        var taken = 0;
                        foreach (var existing in LogMessages)
                        {
                            if (taken >= fromExisting) break;
                            rebuilt.Add(existing);
                            taken++;
                        }
                    }
                    LogMessages.ReplaceAll(rebuilt);
                }
            }

            var status = Interlocked.Exchange(ref _latestStatusMessage, null);
            if (status != null)
                StatusMessage = status;
        });
    }

    /// <summary>
    /// Public method for the View to log messages (e.g., error handling in async void handlers).
    /// </summary>
    public void AddLogMessage(string message) => AddLog(message);

    private void UpdateProgress(BackupProgressEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressText = $"Backed up: {Path.GetFileName(e.FilePath)} ({e.ChunksUploaded}/{e.TotalChunks} chunks)";
            RefreshStatistics();
        });
    }

    #region Events for View Communication

    /// <summary>
    /// Event raised when a folder picker dialog is needed.
    /// The View subscribes to this to show the native folder picker.
    /// </summary>
    public event EventHandler? FolderPickerRequested;

    /// <summary>
    /// Event raised when a restore folder picker dialog is needed.
    /// </summary>
    public event EventHandler? RestoreFolderPickerRequested;

    /// <summary>
    /// Event raised when a remap folder picker dialog is needed.
    /// </summary>
    public event EventHandler? RemapFolderPickerRequested;

    /// <summary>
    /// Event raised when a preview dialog is needed.
    /// The View subscribes to show the preview dialog and returns user's decision.
    /// </summary>
    public event Func<OperationPreviewViewModel, Task<bool>>? PreviewDialogRequested;

    /// <summary>
    /// Shows a preview dialog and returns whether the user confirmed the operation.
    /// </summary>
    protected async Task<bool> ShowPreviewDialogAsync(Core.Models.OperationPreview preview)
    {
        if (PreviewDialogRequested == null)
        {
            // No handler registered, proceed without preview
            AddLog("Warning: Preview dialog not available, proceeding...");
            return true;
        }

        OperationPreviewViewModel viewModel = new(preview);
        return await PreviewDialogRequested.Invoke(viewModel);
    }

    #endregion

    #region Helper Methods

    private void RefreshBackedUpFiles()
    {
        var files = _databaseService.GetAllBackedUpFiles();
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BackedUpFiles.Clear();
            foreach (var file in files.OrderByDescending(f => f.BackedUpAt))
            {
                BackedUpFiles.Add(new BackedUpFileViewModel(file));
            }
        });
    }

    /// <summary>
    /// Notifies the UI that selection-related properties have changed.
    /// </summary>
    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedFilesCount));
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(SelectedFilesText));
    }

    /// <summary>
    /// Handles file selection with support for Ctrl+Click and Shift+Click.
    /// </summary>
    /// <param name="file">The file that was clicked.</param>
    /// <param name="isCtrlPressed">Whether Ctrl key is pressed (toggle selection).</param>
    /// <param name="isShiftPressed">Whether Shift key is pressed (range selection).</param>
    public void HandleFileSelection(BackedUpFileViewModel file, bool isCtrlPressed, bool isShiftPressed)
    {
        if (isShiftPressed && _lastClickedFile != null)
        {
            // Range selection: select all files between last clicked and current
            var startIndex = RestorableFiles.IndexOf(_lastClickedFile);
            var endIndex = RestorableFiles.IndexOf(file);
            
            if (startIndex >= 0 && endIndex >= 0)
            {
                var minIndex = Math.Min(startIndex, endIndex);
                var maxIndex = Math.Max(startIndex, endIndex);
                
                // If Ctrl is not pressed, clear existing selection first
                if (!isCtrlPressed)
                {
                    foreach (var f in RestorableFiles)
                        f.IsSelected = false;
                }
                
                // Select the range
                for (var i = minIndex; i <= maxIndex; i++)
                {
                    RestorableFiles[i].IsSelected = true;
                }
            }
        }
        else if (isCtrlPressed)
        {
            // Toggle selection for this file only
            file.IsSelected = !file.IsSelected;
        }
        else
        {
            // Single click without modifiers: select only this file
            foreach (var f in RestorableFiles)
                f.IsSelected = false;
            file.IsSelected = true;
        }
        
        _lastClickedFile = file;
        SelectedRestoreFile = file;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Selects all files in the restore list.
    /// </summary>
    public void SelectAllFiles()
    {
        foreach (var file in RestorableFiles)
            file.IsSelected = true;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Deselects all files in the restore list.
    /// </summary>
    public void DeselectAllFiles()
    {
        foreach (var file in RestorableFiles)
            file.IsSelected = false;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Toggles selection of a file (used by checkbox binding).
    /// </summary>
    public void ToggleFileSelection(BackedUpFileViewModel file)
    {
        file.IsSelected = !file.IsSelected;
        _lastClickedFile = file;
        NotifySelectionChanged();
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        // Unsubscribe from static events to prevent memory leaks
        LocalFileTreeNodeViewModel.SelectionChanged -= OnLocalFileSelectionChanged;
        FileTreeNodeViewModel.SelectionChanged -= OnAzureFileSelectionChanged;
        
        // Cancel any ongoing operations
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;

        await _orchestrator.DisposeAsync();
        await _blobService.DisposeAsync();
        _encryptionService.Dispose();

        // I6: drain any buffered upload-time MD5s before the database
        // service goes away. Disposed BEFORE _databaseService for that
        // reason; otherwise the drain's last few SetChunkExpectedMd5
        // calls would race against database disposal.
        if (_expectedMd5Drain != null)
        {
            await _expectedMd5Drain.DisposeAsync();
            _expectedMd5Drain = null;
        }

        // Stop the periodic WAL checkpoint timer before disposing the database so
        // a late-firing callback cannot race against a disposed LiteDB instance.
        if (_checkpointTimer is not null)
        {
            await _checkpointTimer.DisposeAsync();
            _checkpointTimer = null;
        }

        // Final explicit checkpoint on clean shutdown - flushes anything still
        // sitting in the WAL into the main data file so the next open is fast.
        try { _databaseService.Checkpoint(); } catch { /* best effort on shutdown */ }

        _databaseService.Dispose();
        _fileWatcherService.Dispose();
        _throughputMetrics.Dispose();
        DataIntegrityTabVm?.Dispose();
    }

    /// <summary>
    /// Lazily creates the Data Integrity ViewModel on first request and
    /// switches the main view to it. Subsequent calls reuse the existing
    /// instance so persisted scope selection and history survive
    /// navigation.
    /// </summary>
    private void OpenDataIntegrityTab()
    {
        if (_integrityService == null) return;

        if (DataIntegrityTabVm == null)
        {
            var vm = new DataIntegrityViewModel(_integrityService, _databaseService)
            {
                SessionStartUtc = Program.Logger?.SessionStartUtc ?? DateTime.UtcNow
            };
            // Bridge: when a check completes, switch to the tab (in case
            // user navigated away mid-run) and post a status line so the
            // notification is visible even if the tab is not in focus.
            vm.CheckCompleted += (_, e) =>
            {
                var totalFailures = e.Result.Run.FilesFailedT1 + e.Result.Run.FilesFailedT2 + e.Result.Run.FilesFailedT3;
                var summary = totalFailures == 0
                    ? $"Integrity check OK -- {e.Result.Run.FilesChecked} files passed."
                    : $"Integrity check found {totalFailures} failures across {e.Result.Run.FilesChecked} files.";
                AddLog(summary);
                StatusMessage = summary;
                CurrentView = "DataIntegrity";
            };
            DataIntegrityTabVm = vm;
            ShowDataIntegrityNavButton = true;
            // Fire-and-forget the initial tree population so opening the
            // tab is instant; the file list appears as soon as the DB
            // read finishes.
            _ = vm.RefreshFileTreeAsync();
        }

        CurrentView = "DataIntegrity";
    }
}
