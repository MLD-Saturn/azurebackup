using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly AzureBlobService _blobService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly BackupOrchestrator _orchestrator;
    private readonly RestoreService _restoreService;
    private ChunkIndexService? _chunkIndexService;

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
        ? "?? Unlock" 
        : "?? Initialize & Connect";

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
    private string _entraIdUserName = string.Empty;
    
    [ObservableProperty]
    private string _entraIdStatus = "Not signed in";

    // Connection String authentication
    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private string _storageAccountName = string.Empty;

    [ObservableProperty]
    private string _containerName = "backup";

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
    private string _lastBackupTime = "Never";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    // Detailed progress tracking
    [ObservableProperty]
    private string _currentOperationType = string.Empty;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private double _currentFileProgress;

    [ObservableProperty]
    private string _currentFileProgressText = string.Empty;

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
    public ObservableCollection<BackedUpFileViewModel> BackedUpFiles { get; } = [];
    public ObservableCollection<BackedUpFileViewModel> RestorableFiles { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];

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
    /// </summary>
    [ObservableProperty]
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

    #region Tree View Properties

    /// <summary>
    /// Root nodes for the file tree view (Azure backup files).
    /// </summary>
    public ObservableCollection<FileTreeNodeViewModel> FileTreeRoots { get; } = [];

    /// <summary>
    /// Root nodes for the local file tree view.
    /// </summary>
    public ObservableCollection<LocalFileTreeNodeViewModel> LocalFileTreeRoots { get; } = [];

    /// <summary>
    /// Flat list of all local files for list view mode.
    /// </summary>
    public ObservableCollection<LocalFileTreeNodeViewModel> LocalFilesFlatList { get; } = [];


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
    /// Text describing what will happen when files are dropped.
    /// </summary>
    [ObservableProperty]
    private string _dragDropPreviewText = string.Empty;

    /// <summary>
    /// Number of files being dragged.
    /// </summary>
    [ObservableProperty]
    private int _dragFileCount;

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
        _blobService = new AzureBlobService(_encryptionService);
        _fileWatcherService = new FileWatcherService(_databaseService);
        _orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, _chunkingService, 
            _blobService, _fileWatcherService);
        _restoreService = new RestoreService(_databaseService, _blobService, _encryptionService);

        // Initialize chunk index service (requires blob service to be connected later)
        _chunkIndexService = new ChunkIndexService(_databaseService, _blobService, _encryptionService);
        _orchestrator.SetChunkIndexService(_chunkIndexService);
        
        // Initialize Storage Health ViewModel
        StorageHealthViewModel = new StorageHealthViewModel(_chunkIndexService, _databaseService);

        // Initialize Tier Migration ViewModel
        TierMigrationViewModel = new TierMigrationViewModel(_blobService, _chunkIndexService, msg => AddLog(msg));

        // Wire up status events
        _orchestrator.StatusChanged += (s, msg) => AddLog(msg);
        _orchestrator.ErrorOccurred += (s, msg) => AddLog($"ERROR: {msg}");
        _orchestrator.ProgressChanged += (s, e) => UpdateProgress(e);

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
        _blobService.DiagnosticLog += OnDiagnosticLog;
        _databaseService.DiagnosticLog += OnDiagnosticLog;
        _encryptionService.DiagnosticLog += OnDiagnosticLog;
        _restoreService.DiagnosticLog += OnDiagnosticLog;
        _fileWatcherService.DiagnosticLog += OnDiagnosticLog;

        // Wire up crash-safe file logger for all service diagnostic events
        if (Program.Logger != null)
        {
            _orchestrator.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _blobService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _databaseService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _encryptionService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _restoreService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            _fileWatcherService.DiagnosticLog += Program.Logger.OnDiagnosticLog;
            
            _restoreService.ErrorOccurred += (s, msg) => Program.Logger.Log($"[ERROR] {msg}");
            _orchestrator.ErrorOccurred += (s, msg) => Program.Logger.Log($"[ERROR] {msg}");
            
            Program.Logger.Log($"Services initialized, log file: {Program.Logger.LogFilePath}");
        }
        
        AddLog("Application started - diagnostic logging enabled");

        // Check database state for returning vs new user
        var dbPath = AppMode.DatabasePath;
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
    /// Handles diagnostic log messages from services.
    /// </summary>
    private void OnDiagnosticLog(object? sender, string message)
    {
        if (EnableDiagnosticLogging)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Add to log with diagnostic prefix
                LogMessages.Add($"?? {message}");
                
                // Keep log size manageable
                while (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(0);
                }
            });
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
        EntraIdUserName = config.EntraIdUserName ?? string.Empty;
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
        LastBackupTime = stats.LastBackupTime?.ToString("g") ?? "Never";
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogMessages.Insert(0, $"[{timestamp}] {message}");
            while (LogMessages.Count > 1000)
                LogMessages.RemoveAt(LogMessages.Count - 1);
            StatusMessage = message;
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

    #region Progress Tracking Helpers

    private DateTime _operationStartTime;
    private long _lastBytesProcessed;
    private DateTime _lastSpeedUpdate;

    /// <summary>
    /// Starts a new operation with progress tracking.
    /// </summary>
    private void StartProgressTracking(string operationType, int totalFiles, long totalBytes)
    {
        _operationStartTime = DateTime.Now;
        _lastBytesProcessed = 0;
        _lastSpeedUpdate = DateTime.Now;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTransferInProgress = true; // Show the progress panel
            CurrentOperationType = operationType;
            TotalFilesInOperation = totalFiles;
            TotalBytesToProcess = totalBytes;
            CompletedFilesCount = 0;
            TotalBytesProcessed = 0;
            ProgressValue = 0;
            CurrentFileName = string.Empty;
            CurrentFileProgress = 0;
            CurrentFileProgressText = string.Empty;
            OperationSpeed = string.Empty;
            EstimatedTimeRemaining = string.Empty;
            _currentFileIndex = 0;
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
        });
    }

    /// <summary>
    /// Updates progress for the current file being processed.
    /// </summary>
    private void UpdateFileProgress(string fileName, long bytesProcessed, long fileSize, int fileIndex)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentFileName = fileName;
            CurrentFileProgress = fileSize > 0 ? (double)bytesProcessed / fileSize * 100 : 0;
            CurrentFileProgressText = $"{AzureBackup.Core.FormatHelper.FormatBytes(bytesProcessed)} / {AzureBackup.Core.FormatHelper.FormatBytes(fileSize)}";
            
            // Update overall progress based on total bytes transferred
            TotalBytesProcessed = _lastBytesProcessed + bytesProcessed;
            ProgressValue = TotalBytesToProcess > 0 
                ? (double)TotalBytesProcessed / TotalBytesToProcess * 100 
                : 0;
            
            // Update files progress to show current file being processed (1-indexed)
            // CompletedFilesCount shows files fully completed, but we also want to show
            // that we're working on file fileIndex+1
            _currentFileIndex = fileIndex;
            
            ProgressText = $"{CurrentOperationType}: {fileName} ({fileIndex + 1}/{TotalFilesInOperation})";
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
            
            // Update speed and ETA periodically
            UpdateSpeedAndEta();
        });
    }

    /// <summary>
    /// Marks a file as completed in the progress tracking.
    /// </summary>
    private void CompleteFileProgress(long fileSize)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CompletedFilesCount++;
            _lastBytesProcessed += fileSize;
            TotalBytesProcessed = _lastBytesProcessed;
            CurrentFileProgress = 100;
            
            // Update progress value based on bytes
            ProgressValue = TotalBytesToProcess > 0 
                ? (double)TotalBytesProcessed / TotalBytesToProcess * 100 
                : 0;
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
        });
    }

    /// <summary>
    /// Updates speed calculation and estimated time remaining.
    /// Speed = Total bytes transferred / Total elapsed time
    /// ETA = Remaining bytes / Speed
    /// </summary>
    private void UpdateSpeedAndEta()
    {
        var now = DateTime.Now;
        var elapsed = now - _operationStartTime;
        
        // Only update speed every 500ms to avoid flickering
        if ((now - _lastSpeedUpdate).TotalMilliseconds < 500)
            return;
        
        _lastSpeedUpdate = now;

        if (elapsed.TotalSeconds > 1 && TotalBytesProcessed > 0)
        {
            var bytesPerSecond = TotalBytesProcessed / elapsed.TotalSeconds;
            OperationSpeed = $"{AzureBackup.Core.FormatHelper.FormatBytes((long)bytesPerSecond)}/s";

            if (bytesPerSecond > 0 && TotalBytesToProcess > TotalBytesProcessed)
            {
                var remainingBytes = TotalBytesToProcess - TotalBytesProcessed;
                var remainingSeconds = remainingBytes / bytesPerSecond;
                EstimatedTimeRemaining = $"{AzureBackup.Core.FormatHelper.FormatDuration(remainingSeconds)} remaining";
            }
        }
    }

    /// <summary>
    /// Clears progress tracking state after operation completes.
    /// </summary>
    private void ClearProgressTracking()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressValue = 0;
            ProgressText = string.Empty;
            CurrentOperationType = string.Empty;
            CurrentFileName = string.Empty;
            CurrentFileProgress = 0;
            CurrentFileProgressText = string.Empty;
            CompletedFilesCount = 0;
            TotalFilesInOperation = 0;
            TotalBytesProcessed = 0;
            TotalBytesToProcess = 0;
            OperationSpeed = string.Empty;
            EstimatedTimeRemaining = string.Empty;
            _currentFileIndex = 0;
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
        });
    }

    /// <summary>
    /// Stops progress tracking and hides the progress panel.
    /// Call this at the end of file transfer operations.
    /// </summary>
    private void StopProgressTracking()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTransferInProgress = false;
            ClearProgressTracking();
        });
    }

    #endregion


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
    /// Event raised when a file picker dialog is needed.
    /// </summary>
    public event EventHandler? FilePickerRequested;

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
        if (isShiftPressed && LastClickedFile != null)
        {
            // Range selection: select all files between last clicked and current
            var startIndex = RestorableFiles.IndexOf(LastClickedFile);
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
        
        LastClickedFile = file;
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
        LastClickedFile = file;
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
        _databaseService.Dispose();
        _fileWatcherService.Dispose();
    }
}
