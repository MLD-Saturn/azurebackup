using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for the Data Integrity tab (D2). Hosts the scope panel
/// (file tree + time/history dropdown), the in-flight progress, and the
/// failure-list panel. The tab is created lazily on first "Check Data
/// Integrity" click from the Storage Health tab and persists for the
/// rest of the app session.
/// </summary>
/// <remarks>
/// <para>
/// Bidirectional sync rule (from the design discussion): selecting a time
/// preset auto-checks matching files in the tree; manually toggling a
/// checkbox flips the dropdown to "(Custom selection)". The mechanism is
/// the static <see cref="IntegrityFileTreeNodeViewModel.SelectionChanged"/>
/// event subscribed in the constructor. A guard flag
/// (<see cref="_applyingPreset"/>) prevents preset-driven selection
/// changes from re-entering the dropdown reset path.
/// </para>
/// <para>
/// Concurrency invariants mirror <see cref="StorageHealthViewModel"/>:
/// one in-flight operation at a time gated by
/// <see cref="IsOperationInProgress"/>; the
/// <c>BeginOperation</c> / <c>EndOperation</c> CTS pattern handles cancel.
/// </para>
/// </remarks>
public partial class DataIntegrityViewModel : ViewModelBase, IDisposable
{
    private readonly IntegrityCheckService _integrityService;
    private readonly LocalDatabaseService _databaseService;
    private CancellationTokenSource? _operationCts;
    private bool _applyingPreset; // bidirectional-sync re-entrancy guard
    private bool _disposed;

    public DataIntegrityViewModel(
        IntegrityCheckService integrityService,
        LocalDatabaseService databaseService)
    {
        _integrityService = integrityService ?? throw new ArgumentNullException(nameof(integrityService));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));

        // Subscribe to the static selection-changed event so that any
        // manual checkbox toggle resets the time/history dropdown to
        // "(Custom)". The check IsApplyingPreset short-circuits when WE
        // were the source of the toggle (preset application) so the
        // dropdown isn't reset by its own programmatic update.
        IntegrityFileTreeNodeViewModel.SelectionChanged += OnTreeSelectionChanged;

        // Default scope preset: this session.
        SelectedScopePreset = ScopeOptions[0];
    }

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCheck))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isOperationInProgress;

    [ObservableProperty]
    private string _statusMessage = "Ready -- select a scope preset or check files manually, then press Check Now.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private int _t1FailCount;

    [ObservableProperty]
    private int _t2FailCount;

    [ObservableProperty]
    private int _t3FailCount;

    /// <summary>Selected scope-preset string (drives the file tree).</summary>
    [ObservableProperty]
    private string _selectedScopePreset = string.Empty;

    /// <summary>Whether the AutoExportBundleOnFailure option is on for the next run.</summary>
    [ObservableProperty]
    private bool _autoExportBundleOnFailure = true;

    /// <summary>True when at least one file is selected in the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCheck))]
    private int _selectedFileCount;

    public ObservableCollection<IntegrityFileTreeNodeViewModel> FileTreeRoots { get; } = [];

    /// <summary>Failures from the most recent run, grouped by tier for the UI.</summary>
    public ObservableCollection<IntegrityFailureGroupViewModel> FailureGroups { get; } = [];

    public ObservableCollection<IntegrityCheckRunViewModel> RunHistory { get; } = [];

    /// <summary>The full list of available scope presets shown in the dropdown.</summary>
    public string[] ScopeOptions { get; } =
    [
        "This session",
        "Last 24 hours",
        "Last 7 days",
        "All files",
        "Files that failed last run",
        "(Custom selection)"
    ];

    #endregion

    #region Computed Properties

    public bool CanRunCheck => !IsOperationInProgress && SelectedFileCount > 0;
    public bool CanCancel => IsOperationInProgress;
    public bool HasFailures => FailureGroups.Count > 0;
    public bool HasRunHistory => RunHistory.Count > 0;

    #endregion

    /// <summary>
    /// Per-app-session anchor for the "This session" preset. Set by
    /// <see cref="MainWindowViewModel"/> from the
    /// <c>CrashSafeLogger.SessionId</c> launch time. Defaults to "now"
    /// when not set so a unit-test env still has a sensible value.
    /// </summary>
    public DateTime SessionStartUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Loads the file tree from the persisted backup corpus. Called on
    /// first navigation to the tab and any time the user clicks Refresh.
    /// </summary>
    public async Task RefreshFileTreeAsync()
    {
        var files = await Task.Run(() => _databaseService.GetAllBackedUpFiles());
        var roots = IntegrityFileTreeNodeViewModel.BuildTree(files);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FileTreeRoots.Clear();
            foreach (var r in roots) FileTreeRoots.Add(r);
            ApplyScopePreset(SelectedScopePreset);
            RefreshHistory();
        });
    }

    /// <summary>
    /// Recompute the failure-list pane from the most recent persisted run.
    /// Called after a check completes and on app start (to restore last
    /// state since the tab persists indefinitely).
    /// </summary>
    public void RefreshHistory()
    {
        RunHistory.Clear();
        var runs = _databaseService.GetRecentIntegrityCheckRuns(limit: 10);
        foreach (var r in runs)
        {
            RunHistory.Add(new IntegrityCheckRunViewModel(r));
        }
        OnPropertyChanged(nameof(HasRunHistory));

        FailureGroups.Clear();
        var latest = runs.FirstOrDefault();
        if (latest != null)
        {
            var failures = _databaseService.GetIntegrityCheckFailures(latest.Id);
            PopulateFailureGroups(failures);
        }
        OnPropertyChanged(nameof(HasFailures));
    }

    private void PopulateFailureGroups(IList<IntegrityCheckFailure> failures)
    {
        // Group by tier, deepest tier first (T3 most damning at the top).
        foreach (var grouping in failures.GroupBy(f => f.FailureTier).OrderByDescending(g => g.Key))
        {
            var group = new IntegrityFailureGroupViewModel(grouping.Key, grouping.ToList());
            FailureGroups.Add(group);
        }
    }

    partial void OnSelectedScopePresetChanged(string value)
    {
        ApplyScopePreset(value);
    }

    /// <summary>
    /// Applies the named preset to the file-tree checkboxes. Sets the
    /// re-entrancy guard so the propagated SelectionChanged events do
    /// not flip the dropdown back to "(Custom)".
    /// </summary>
    private void ApplyScopePreset(string preset)
    {
        if (preset == "(Custom selection)") return; // user-driven: do nothing
        if (FileTreeRoots.Count == 0) return;

        _applyingPreset = true;
        try
        {
            DateTime cutoff = preset switch
            {
                "This session" => SessionStartUtc,
                "Last 24 hours" => DateTime.UtcNow.AddDays(-1),
                "Last 7 days" => DateTime.UtcNow.AddDays(-7),
                _ => DateTime.MinValue // "All files" or "Files that failed last run"
            };

            HashSet<int>? failedFileIds = null;
            if (preset == "Files that failed last run")
            {
                var lastRun = _databaseService.GetRecentIntegrityCheckRuns(1).FirstOrDefault();
                if (lastRun != null)
                {
                    failedFileIds = _databaseService.GetIntegrityCheckFailures(lastRun.Id)
                        .Select(f => f.FileId).ToHashSet();
                }
                else
                {
                    failedFileIds = new HashSet<int>();
                }
            }

            // Walk every leaf in the tree and toggle accordingly.
            foreach (var root in FileTreeRoots)
            {
                foreach (var leaf in root.GetAllFiles())
                {
                    if (leaf.File == null) continue;
                    bool match = failedFileIds != null
                        ? failedFileIds.Contains(leaf.File.Id)
                        : leaf.BackedUpAt.ToUniversalTime() >= cutoff;
                    leaf.IsSelected = match;
                }
            }
        }
        finally
        {
            _applyingPreset = false;
            UpdateSelectedFileCount();
        }
    }

    private void OnTreeSelectionChanged(object? sender, EventArgs e)
    {
        if (_applyingPreset) return;

        // User-driven toggle -- flip the dropdown without re-entering ApplyScopePreset.
        if (SelectedScopePreset != "(Custom selection)")
        {
            _applyingPreset = true; // suppress preset re-application
            try { SelectedScopePreset = "(Custom selection)"; }
            finally { _applyingPreset = false; }
        }
        UpdateSelectedFileCount();
    }

    private void UpdateSelectedFileCount()
    {
        SelectedFileCount = FileTreeRoots.SelectMany(r => r.GetAllFiles())
            .Count(f => f.IsSelected && f.File != null);
    }

    private CancellationToken BeginOperation()
    {
        var prev = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
        prev?.Dispose();
        return _operationCts!.Token;
    }

    private void EndOperation()
    {
        var current = Interlocked.Exchange(ref _operationCts, null);
        current?.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckNowAsync()
    {
        var fileIds = FileTreeRoots
            .SelectMany(r => r.GetAllFiles())
            .Where(n => n.IsSelected && n.File != null)
            .Select(n => n.File!.Id)
            .ToList();
        if (fileIds.Count == 0) return;

        IsOperationInProgress = true;
        var ct = BeginOperation();
        StatusMessage = $"Running integrity check on {fileIds.Count} file(s)...";
        ProgressValue = 0;
        T1FailCount = 0;
        T2FailCount = 0;
        T3FailCount = 0;

        var scopeSummary = $"{SelectedScopePreset} ({fileIds.Count} files)";
        var options = new IntegrityCheckOptions
        {
            FileIds = fileIds,
            ScopeSummary = scopeSummary,
            AutoExportBundleOnFailure = AutoExportBundleOnFailure
        };

        var progress = new Progress<IntegrityCheckProgress>(p =>
        {
            ProgressValue = p.FilesTotal > 0 ? (double)p.FilesProcessed / p.FilesTotal * 100 : 0;
            ProgressText = $"{p.FilesProcessed}/{p.FilesTotal} -- {p.CurrentFile}";
            T1FailCount = p.T1FailCount;
            T2FailCount = p.T2FailCount;
            T3FailCount = p.T3FailCount;
        });

        try
        {
            var result = await _integrityService.RunAsync(options, progress, ct);
            var totalFailures = result.Run.FilesFailedT1 + result.Run.FilesFailedT2 + result.Run.FilesFailedT3;
            StatusMessage = result.Run.Cancelled
                ? $"Cancelled at {result.Run.FilesChecked} files ({totalFailures} failures)."
                : totalFailures == 0
                    ? $"All {result.Run.FilesChecked} files passed."
                    : $"{result.Run.FilesChecked} files checked -- {totalFailures} failed " +
                      $"(T1={result.Run.FilesFailedT1}, T2={result.Run.FilesFailedT2}, T3={result.Run.FilesFailedT3}).";

            // Notify host so it can pop a toast + switch to this tab.
            CheckCompleted?.Invoke(this, new CheckCompletedEventArgs(result));
            RefreshHistory();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Check failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            EndOperation();
            IsOperationInProgress = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _operationCts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    /// <summary>
    /// Re-check the failures from <paramref name="parentRun"/>. New scope =
    /// just the failing FileIds; new run row carries ParentRunId for
    /// lineage in the History expander.
    /// </summary>
    public async Task ReCheckFailuresAsync(IntegrityCheckRun parentRun)
    {
        ArgumentNullException.ThrowIfNull(parentRun);
        var failures = _databaseService.GetIntegrityCheckFailures(parentRun.Id);
        var ids = failures.Select(f => f.FileId).Distinct().ToList();
        if (ids.Count == 0)
        {
            StatusMessage = $"Run #{parentRun.Id} has no failures to re-check.";
            return;
        }

        IsOperationInProgress = true;
        var ct = BeginOperation();
        StatusMessage = $"Re-checking {ids.Count} failed file(s) from run #{parentRun.Id}...";

        var options = new IntegrityCheckOptions
        {
            FileIds = ids,
            ScopeSummary = $"Re-check failures from run #{parentRun.Id} ({ids.Count} files)",
            IsReCheckOfFailures = true,
            ParentRunId = parentRun.Id,
            AutoExportBundleOnFailure = AutoExportBundleOnFailure
        };

        var progress = new Progress<IntegrityCheckProgress>(p =>
        {
            ProgressValue = p.FilesTotal > 0 ? (double)p.FilesProcessed / p.FilesTotal * 100 : 0;
            ProgressText = $"{p.FilesProcessed}/{p.FilesTotal}";
        });

        try
        {
            var result = await _integrityService.RunAsync(options, progress, ct);
            var totalFailures = result.Run.FilesFailedT1 + result.Run.FilesFailedT2 + result.Run.FilesFailedT3;
            StatusMessage = totalFailures == 0
                ? $"Re-check passed: all {result.Run.FilesChecked} previously-failing files now clean."
                : $"Re-check: {totalFailures} of {result.Run.FilesChecked} files still failing.";
            CheckCompleted?.Invoke(this, new CheckCompletedEventArgs(result));
            RefreshHistory();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Re-check failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            IsOperationInProgress = false;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Raised when an integrity-check run finishes (success, failure, or
    /// cancellation). The host (<see cref="MainWindowViewModel"/>)
    /// switches to the Data Integrity tab and shows a toast.
    /// </summary>
    public event EventHandler<CheckCompletedEventArgs>? CheckCompleted;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IntegrityFileTreeNodeViewModel.SelectionChanged -= OnTreeSelectionChanged;
        _operationCts?.Dispose();
    }
}

/// <summary>Args for <see cref="DataIntegrityViewModel.CheckCompleted"/>.</summary>
public sealed class CheckCompletedEventArgs(IntegrityCheckResult result) : EventArgs
{
    public IntegrityCheckResult Result { get; } = result;
}

/// <summary>
/// Failure-list group rendered as an expander section (one per tier).
/// </summary>
public sealed class IntegrityFailureGroupViewModel
{
    public int Tier { get; }
    public string TierLabel { get; }
    public IReadOnlyList<IntegrityFailureItemViewModel> Items { get; }
    public string Header => $"T{Tier} ({Items.Count}) -- {TierLabel}";

    public IntegrityFailureGroupViewModel(int tier, IList<IntegrityCheckFailure> failures)
    {
        Tier = tier;
        TierLabel = tier switch
        {
            3 => "byte-level mismatch",
            2 => "envelope CRC / decrypt fault",
            1 => "structural / size / missing-blob",
            _ => "other"
        };
        Items = failures.Select(f => new IntegrityFailureItemViewModel(f)).ToList();
    }
}

public sealed class IntegrityFailureItemViewModel
{
    public IntegrityCheckFailure Failure { get; }
    public string DisplayPath => Failure.LocalPath;
    public string Reason => Failure.FailureReason;
    public string ChunkLabel => string.IsNullOrEmpty(Failure.ChunkHash)
        ? "(file-scope)"
        : $"chunk {Failure.ChunkHash[..Math.Min(16, Failure.ChunkHash.Length)]}...";
    public string DiagPath => Failure.DiagFilePath ?? "(no .diag)";

    public IntegrityFailureItemViewModel(IntegrityCheckFailure failure)
    {
        Failure = failure;
    }
}

public sealed class IntegrityCheckRunViewModel
{
    public IntegrityCheckRun Run { get; }
    public string Header
    {
        get
        {
            var totalFailures = Run.FilesFailedT1 + Run.FilesFailedT2 + Run.FilesFailedT3;
            var status = Run.Cancelled ? "[cancelled]" : totalFailures == 0 ? "[ok]" : $"[{totalFailures} failed]";
            return $"#{Run.Id}  {Run.StartedUtc.ToLocalTime():g}  {status}  {Run.ScopeSummary}";
        }
    }

    public IntegrityCheckRunViewModel(IntegrityCheckRun run) { Run = run; }
}
