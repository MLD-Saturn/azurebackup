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
    [NotifyPropertyChangedFor(nameof(CanReCheckFailures))]
    // B20: PropertyChanged on a computed CanXxx is necessary but not
    // sufficient -- Avalonia's Button only re-queries CanExecute when
    // the ICommand raises CanExecuteChanged. The source-generated
    // RelayCommand only fires that event when explicitly told via
    // these attributes (or a manual NotifyCanExecuteChanged() call).
    // Without them, Check Now stayed greyed forever even with files
    // selected; Cancel stayed enabled after a check finished; etc.
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReCheckFailuresOfSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackfillLegacyMd5Command))]
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

    /// <summary>
    /// B43: whether the integrity check engine is allowed to silently
    /// re-upload and re-check files that fail with a repairable reason
    /// (missing-blob, wrong-size, md5-mismatch, crc-mismatch,
    /// decrypt-failed, byte-differ). Default ON matches
    /// <see cref="IntegrityCheckOptions.AutoRepairOnFailure"/>.
    /// Bound to a checkbox on the Data Integrity tab; the user can
    /// disable it for a forensic run that wants to see the un-repaired
    /// failure shape.
    /// </summary>
    [ObservableProperty]
    private bool _autoRepairOnFailure = true;

    /// <summary>
    /// D10: count of chunks awaiting MD5 backfill. Drives the visibility
    /// and label of the "Promote pre-D6 chunks" button. Refreshed on
    /// tree-load and after each backfill run.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackfillWork))]
    [NotifyPropertyChangedFor(nameof(BackfillButtonLabel))]
    // B20: ditto -- the Backfill button stayed at its initial state
    // (greyed if LegacyChunkCount started at 0, enabled if it started
    // > 0) across the entire app session, regardless of subsequent
    // re-scans that updated the count.
    [NotifyCanExecuteChangedFor(nameof(BackfillLegacyMd5Command))]
    private long _legacyChunkCount;

    public bool HasBackfillWork => LegacyChunkCount > 0 && !IsOperationInProgress;
    public string BackfillButtonLabel => $"Promote {LegacyChunkCount:N0} pre-D6 chunk(s)";

    /// <summary>True when at least one file is selected in the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCheck))]
    // B20: see _isOperationInProgress comment. Without this attribute the
    // Check Now button would stay greyed even after the user explicitly
    // checked file rows in the tree.
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private int _selectedFileCount;

    /// <summary>
    /// History row currently displayed in the failures pane. Set by the
    /// view's row-click handler; null means "show the latest run". The
    /// pane reloads from <see cref="LocalDatabaseService.GetIntegrityCheckFailures"/>
    /// each time this changes so the user can browse historical results
    /// without re-running anything.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReCheckFailures))]
    private IntegrityCheckRunViewModel? _selectedRun;

    public ObservableCollection<IntegrityFileTreeNodeViewModel> FileTreeRoots { get; } = [];

    /// <summary>Failures from the most recent run, grouped by tier for the UI.</summary>
    public ObservableCollection<IntegrityFailureGroupViewModel> FailureGroups { get; } = [];

    public ObservableCollection<IntegrityCheckRunViewModel> RunHistory { get; } = [];

    /// <summary>
    /// Sentinel scope-preset value assigned to <see cref="SelectedScopePreset"/>
    /// when the user manually toggles a checkbox in the file tree. NOT part
    /// of the dropdown's <see cref="ScopeOptions"/> -- the user can never
    /// pick it, only land on it via tree manipulation. Avalonia's ComboBox
    /// happily displays a bound value that is not in its ItemsSource
    /// without highlighting any item, which is exactly the affordance we
    /// want for a "current state" indicator.
    /// </summary>
    public const string CustomScopeSentinel = "(Custom selection)";

    /// <summary>The scope presets shown in the dropdown. The user picks
    /// one of these; <see cref="CustomScopeSentinel"/> is set programmatically
    /// when the user diverges from any preset by toggling checkboxes.</summary>
    public string[] ScopeOptions { get; } =
    [
        "This session",
        "Last 24 hours",
        "Last 7 days",
        "All files",
        "Files that failed last run"
    ];

    #endregion

    #region Computed Properties

    public bool CanRunCheck => !IsOperationInProgress && SelectedFileCount > 0;
    public bool CanCancel => IsOperationInProgress;
    public bool HasFailures => FailureGroups.Count > 0;
    public bool HasRunHistory => RunHistory.Count > 0;

    /// <summary>
    /// True when the backup catalogue has at least one file. Drives the
    /// empty-state TextBlock in the file-tree panel (D7 review fix 2.5).
    /// </summary>
    public bool HasFiles => FileTreeRoots.Count > 0;

    /// <summary>
    /// True when there is a non-empty failure list to re-check (either
    /// the latest run or a historical run selected in the History
    /// expander) and no operation is currently running.
    /// </summary>
    public bool CanReCheckFailures => !IsOperationInProgress && FailureGroups.Count > 0;

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
    /// Exposed as a RelayCommand so the view can bind a Refresh button
    /// (D7 review fix 3.4) -- after backing up new files in another tab,
    /// the user can repopulate without restarting.
    /// </summary>
    [RelayCommand]
    public async Task RefreshFileTreeAsync()
    {
        var files = await Task.Run(() => _databaseService.GetAllBackedUpFiles());
        var roots = IntegrityFileTreeNodeViewModel.BuildTree(files);
        // D10: refresh the legacy-chunk count alongside the tree so the
        // backfill button reflects current state.
        var legacyCount = await Task.Run(() => _databaseService.CountChunksWithNullExpectedMd5());

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FileTreeRoots.Clear();
            foreach (var r in roots) FileTreeRoots.Add(r);
            OnPropertyChanged(nameof(HasFiles));
            LegacyChunkCount = legacyCount;
            ApplyScopePreset(SelectedScopePreset);
            RefreshHistory();
        });
    }

    /// <summary>
    /// D10: one-shot scan that promotes pre-D6 chunks by running T2
    /// download + envelope verify on each chunk with a null
    /// expected_encrypted_md5, then stamping the MD5 only on
    /// successful verification. Closes the TOFU window vulnerability
    /// where a chunk that was already corrupt at the time of first
    /// integrity check would have its corrupt MD5 captured as
    /// "expected" and pass forever after.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasBackfillWork))]
    private async Task BackfillLegacyMd5Async()
    {
        IsOperationInProgress = true;
        ProgressText = $"Promoting {LegacyChunkCount:N0} pre-D6 chunk(s)...";
        OnPropertyChanged(nameof(HasBackfillWork));
        try
        {
            var progress = new Progress<LegacyMd5BackfillProgress>(p =>
            {
                ProgressText = $"Promoting {p.Processed:N0} / {p.Total:N0}  " +
                               $"(promoted={p.Promoted:N0}, failed={p.Failed:N0})";
            });
            var result = await _integrityService.BackfillLegacyMd5Async(progress);
            ProgressText = $"Backfill complete: promoted {result.Promoted:N0}, " +
                           $"failed {result.Failed:N0} of {result.Total:N0} chunk(s).";
            // Refresh the count so the button hides if all promoted, or
            // shows the remaining count if some failed (so user can retry).
            LegacyChunkCount = await Task.Run(() => _databaseService.CountChunksWithNullExpectedMd5());
        }
        catch (Exception ex)
        {
            ProgressText = $"Backfill failed: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
            OnPropertyChanged(nameof(HasBackfillWork));
        }
    }

    /// <summary>
    /// Recompute the failure-list pane from the most recent persisted run.
    /// Called after a check completes and on app start (to restore last
    /// state since the tab persists indefinitely). Reads run via
    /// <see cref="Task.Run"/> so a large failures table does not hitch
    /// the UI thread (D7 review fix 1.4).
    /// </summary>
    public void RefreshHistory()
    {
        // Run the DB reads on a background thread; marshal updates back
        // through Avalonia's dispatcher. Fire-and-forget is acceptable
        // here -- a slow refresh just delays the visible update; it
        // cannot corrupt state.
        _ = Task.Run(() =>
        {
            var runs = _databaseService.GetRecentIntegrityCheckRuns(limit: 10);
            var latest = runs.FirstOrDefault();
            var failures = latest != null
                ? _databaseService.GetIntegrityCheckFailures(latest.Id)
                : new List<IntegrityCheckFailure>();

            Dispatcher.UIThread.Post(() =>
            {
                RunHistory.Clear();
                foreach (var r in runs)
                {
                    RunHistory.Add(new IntegrityCheckRunViewModel(r));
                }
                OnPropertyChanged(nameof(HasRunHistory));

                FailureGroups.Clear();
                if (latest != null)
                {
                    PopulateFailureGroups(failures);
                }
                OnPropertyChanged(nameof(HasFailures));

                // D7 review fix 1.7: clear the user's prior History row
                // selection because the VM instances they pointed at have
                // just been replaced. Setting null intentionally triggers
                // OnSelectedRunChanged which redirects the failures pane
                // back to the latest run.
                SelectedRun = null;
            });
        });
    }

    private void PopulateFailureGroups(IList<IntegrityCheckFailure> failures)
    {
        // Group by tier, deepest tier first (T3 most damning at the top).
        foreach (var grouping in failures.GroupBy(f => f.FailureTier).OrderByDescending(g => g.Key))
        {
            var group = new IntegrityFailureGroupViewModel(grouping.Key, grouping.ToList());
            FailureGroups.Add(group);
        }
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(CanReCheckFailures));
        ReCheckFailuresOfSelectedCommand.NotifyCanExecuteChanged();
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
        if (preset == CustomScopeSentinel) return; // user-driven: do nothing
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
        if (SelectedScopePreset != CustomScopeSentinel)
        {
            _applyingPreset = true; // suppress preset re-application
            try { SelectedScopePreset = CustomScopeSentinel; }
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
            AutoExportBundleOnFailure = AutoExportBundleOnFailure,
            AutoRepairOnFailure = AutoRepairOnFailure
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
        catch (DatabaseFileCorruptException ex)
        {
            // B44: the integrity-check engine never ran -- the local catalog
            // file is corrupted on disk and we could not even record the run.
            // Surface the actionable guidance from the exception verbatim;
            // the engine's message already names the Storage Health remediation.
            StatusMessage = $"Check could not start: {ex.Message}";
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
        // I2: hint to the user that in-flight per-file workers may take
        // a second or two to honour the cancel (Parallel.ForEachAsync
        // lets each in-progress iteration finish its current chunk
        // before bailing out). Without this, "Cancelling..." sitting
        // unchanged for ~2 s looks like a hang.
        StatusMessage = "Cancelling - finishing current chunks...";
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
            AutoExportBundleOnFailure = AutoExportBundleOnFailure,
            AutoRepairOnFailure = AutoRepairOnFailure
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
        catch (DatabaseFileCorruptException ex)
        {
            StatusMessage = $"Re-check could not start: {ex.Message}";
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
    /// UI command: re-check failures of the currently-selected History row,
    /// or the most recent run when no row is selected. Same operation type
    /// as a fresh check; the new run's <see cref="IntegrityCheckRun.ParentRunId"/>
    /// records the lineage so the History expander shows it as a child.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReCheckFailures))]
    private async Task ReCheckFailuresOfSelected()
    {
        var target = SelectedRun?.Run
            ?? _databaseService.GetRecentIntegrityCheckRuns(1).FirstOrDefault();
        if (target == null)
        {
            StatusMessage = "No run available to re-check.";
            return;
        }
        await ReCheckFailuresAsync(target);
    }

    partial void OnSelectedRunChanged(IntegrityCheckRunViewModel? value)
    {
        // D7 review fix 1.3: move DB read off the UI thread. A run with
        // thousands of failure rows would visibly hitch the click.
        var runId = value?.Run.Id ?? 0;
        _ = Task.Run(() =>
        {
            // null SelectedRun => fall back to latest run (the on-tab-
            // open default). Otherwise show the requested historical run.
            var effectiveRunId = runId > 0
                ? runId
                : _databaseService.GetRecentIntegrityCheckRuns(1).FirstOrDefault()?.Id ?? 0;
            var failures = effectiveRunId > 0
                ? _databaseService.GetIntegrityCheckFailures(effectiveRunId)
                : new List<IntegrityCheckFailure>();

            Dispatcher.UIThread.Post(() =>
            {
                FailureGroups.Clear();
                if (effectiveRunId > 0)
                {
                    PopulateFailureGroups(failures);
                }
                OnPropertyChanged(nameof(HasFailures));
                OnPropertyChanged(nameof(CanReCheckFailures));
            });
        });
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

public partial class IntegrityFailureItemViewModel : ObservableObject
{
    public IntegrityCheckFailure Failure { get; }
    public string DisplayPath => Failure.LocalPath;
    public string Reason => Failure.FailureReason;
    public string ChunkLabel => string.IsNullOrEmpty(Failure.ChunkHash)
        ? "(file-scope)"
        : $"chunk {Failure.ChunkHash[..Math.Min(16, Failure.ChunkHash.Length)]}...";
    public string DiagPath => Failure.DiagFilePath ?? "(no .diag)";
    public bool HasDiag => !string.IsNullOrEmpty(Failure.DiagFilePath) && System.IO.File.Exists(Failure.DiagFilePath);

    public IntegrityFailureItemViewModel(IntegrityCheckFailure failure)
    {
        Failure = failure;
    }

    /// <summary>
    /// Opens the .diag file in the system default text editor (D5 review
    /// fix 2.8). Uses <c>UseShellExecute = true</c> so the shell picks the
    /// default editor (Notepad on Windows, TextEdit on macOS, xdg-open on
    /// Linux). Failures are silent because this is a best-effort UX hook;
    /// the path is shown in the row regardless so the user can still copy
    /// it manually.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDiag))]
    private void OpenDiag()
    {
        if (!HasDiag) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Failure.DiagFilePath!)
            {
                UseShellExecute = true
            });
        }
        catch { /* best effort -- the path is shown in the row regardless */ }
    }

    /// <summary>
    /// Reveals the .diag file in the OS file manager (Explorer / Finder /
    /// Files). On Windows uses <c>explorer /select,</c> to highlight the
    /// file inside its containing folder; on other platforms falls back
    /// to opening the parent directory.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDiag))]
    private void ShowInFolder()
    {
        if (!HasDiag) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Failure.DiagFilePath}\"");
            }
            else
            {
                var dir = System.IO.Path.GetDirectoryName(Failure.DiagFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
                    {
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Copies the .diag file path to the system clipboard. Caller (the
    /// view) handles the actual clipboard call because Avalonia's
    /// clipboard API is window-scoped; the VM just exposes the string.
    /// </summary>
    public string PathForCopy => Failure.DiagFilePath ?? string.Empty;
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
            // B42: surface auto-repair count when the most recent run
            // healed at least one file. The value lives only on the
            // in-memory Run instance from the run that produced it
            // (not persisted), so historical rows always show the
            // bare header.
            var repaired = Run.FilesAutoRepaired > 0 ? $" [auto-repaired {Run.FilesAutoRepaired}]" : string.Empty;
            return $"#{Run.Id}  {Run.StartedUtc.ToLocalTime():g}  {status}{repaired}  {Run.ScopeSummary}";
        }
    }

    public IntegrityCheckRunViewModel(IntegrityCheckRun run) { Run = run; }
}
