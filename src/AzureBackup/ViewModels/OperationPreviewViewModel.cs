using System.Collections.ObjectModel;
using System.Linq;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for the operation preview dialog.
/// </summary>
public partial class OperationPreviewViewModel : ObservableObject
{
    private readonly OperationPreview _preview;

    /// <summary>
    /// The underlying preview data.
    /// </summary>
    public OperationPreview Preview => _preview;

    /// <summary>
    /// Title for the dialog.
    /// </summary>
    public string Title => _preview.OperationType switch
    {
        OperationType.Restore => "Restore Preview",
        OperationType.DeleteFromAzure => "Delete Preview",
        OperationType.MirrorSync => "Mirror Sync Preview",
        OperationType.Backup => "Backup Preview",
        _ => "Operation Preview"
    };

    /// <summary>
    /// Icon for the operation type.
    /// </summary>
    public string Icon => _preview.OperationType switch
    {
        OperationType.Restore => "??",
        OperationType.DeleteFromAzure => "???",
        OperationType.MirrorSync => "??",
        OperationType.Backup => "??",
        _ => "??"
    };

    /// <summary>
    /// Operation description.
    /// </summary>
    public string Description => _preview.OperationDescription;

    /// <summary>
    /// Source description.
    /// </summary>
    public string Source => _preview.SourceDescription;

    /// <summary>
    /// Target description.
    /// </summary>
    public string Target => _preview.TargetDescription;

    /// <summary>
    /// Warning message if any.
    /// </summary>
    public string? WarningMessage => _preview.WarningMessage;

    /// <summary>
    /// Whether there's a warning.
    /// </summary>
    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    /// <summary>
    /// Whether the operation has any changes.
    /// </summary>
    public bool HasChanges => _preview.HasChanges;

    /// <summary>
    /// Whether the operation has destructive actions.
    /// </summary>
    public bool HasDestructiveActions => _preview.HasDestructiveActions;

    /// <summary>
    /// Summary text for the operation (reflects included files only).
    /// </summary>
    public string Summary
    {
        get
        {
            System.Collections.Generic.List<string> parts = new();

            var createCount = _preview.IncludedCreateCount;
            var overwriteCount = _preview.IncludedOverwriteCount;
            var deleteCount = _preview.IncludedDeleteCount;
            var skipCount = _preview.SkipCount;

            if (createCount > 0)
                parts.Add($"{createCount} new");
            if (overwriteCount > 0)
                parts.Add($"{overwriteCount} overwrite");
            if (deleteCount > 0)
                parts.Add($"{deleteCount} delete");
            if (skipCount > 0)
                parts.Add($"{skipCount} unchanged");

            return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
        }
    }

    /// <summary>
    /// Total bytes to transfer formatted (included files only).
    /// </summary>
    public string TransferSize => AzureBackup.Core.FormatHelper.FormatBytes(_preview.TotalBytesToTransfer);

    /// <summary>
    /// Total bytes to delete formatted (included files only).
    /// </summary>
    public string DeleteSize => AzureBackup.Core.FormatHelper.FormatBytes(_preview.TotalBytesToDelete);

    /// <summary>
    /// Included create count for display in section headers.
    /// </summary>
    public string IncludedCreateText => 
        $"({_preview.IncludedCreateCount}/{_preview.CreateCount})";

    /// <summary>
    /// Included overwrite count for display in section headers.
    /// </summary>
    public string IncludedOverwriteText => 
        $"({_preview.IncludedOverwriteCount}/{_preview.OverwriteCount})";

    /// <summary>
    /// Included delete count for display in section headers.
    /// </summary>
    public string IncludedDeleteText => 
        $"({_preview.IncludedDeleteCount}/{_preview.DeleteCount})";

    /// <summary>
    /// Files to create.
    /// </summary>
    public ObservableCollection<PreviewFileAction> FilesToCreate { get; }

    /// <summary>
    /// Files to overwrite.
    /// </summary>
    public ObservableCollection<PreviewFileAction> FilesToOverwrite { get; }

    /// <summary>
    /// Files to delete.
    /// </summary>
    public ObservableCollection<PreviewFileAction> FilesToDelete { get; }

    /// <summary>
    /// Files to skip.
    /// </summary>
    public ObservableCollection<PreviewFileAction> FilesToSkip { get; }

    /// <summary>
    /// Whether to show the create section.
    /// </summary>
    public bool ShowCreateSection => _preview.CreateCount > 0;

    /// <summary>
    /// Whether to show the overwrite section.
    /// </summary>
    public bool ShowOverwriteSection => _preview.OverwriteCount > 0;

    /// <summary>
    /// Whether to show the delete section.
    /// </summary>
    public bool ShowDeleteSection => _preview.DeleteCount > 0;

    /// <summary>
    /// Whether to show the skip section.
    /// </summary>
    public bool ShowSkipSection => _preview.SkipCount > 0;

    /// <summary>
    /// Whether the confirm button should be enabled (at least one file included).
    /// </summary>
    public bool CanConfirm => 
        _preview.IncludedCreateCount > 0 || 
        _preview.IncludedOverwriteCount > 0 || 
        _preview.IncludedDeleteCount > 0;

    /// <summary>
    /// Text for the confirm button.
    /// </summary>
    public string ConfirmButtonText => _preview.OperationType switch
    {
        OperationType.Restore => "Restore",
        OperationType.DeleteFromAzure => "Delete",
        OperationType.MirrorSync => "Sync",
        OperationType.Backup => "Backup",
        _ => "Proceed"
    };

    /// <summary>
    /// Style hint for the confirm button (danger for destructive operations).
    /// </summary>
    public bool IsDangerousOperation => 
        _preview.OperationType == OperationType.DeleteFromAzure || 
        (_preview.OperationType == OperationType.MirrorSync && _preview.DeleteCount > 0);

    /// <summary>
    /// Whether this is a backup operation (shows storage tier selector).
    /// </summary>
    public bool IsBackupOperation => _preview.OperationType == OperationType.Backup;

    /// <summary>
    /// Default storage tier from watched folder settings.
    /// </summary>
    public StorageTier DefaultStorageTier => _preview.DefaultStorageTier;

    /// <summary>
    /// Available storage tier options.
    /// </summary>
    public static StorageTier[] StorageTierOptions { get; } = [StorageTier.Hot, StorageTier.Cool, StorageTier.Cold];

    /// <summary>
    /// User-selected storage tier for this backup operation.
    /// </summary>
    [ObservableProperty]
    private StorageTier _selectedStorageTier;

    /// <summary>
    /// Description of the selected storage tier.
    /// </summary>
    public string StorageTierDescription => SelectedStorageTier switch
    {
        StorageTier.Hot => "Hot - Highest cost, fastest access",
        StorageTier.Cool => "Cool - Lower cost, good for backups (recommended)",
        StorageTier.Cold => "Cold - Lowest cost, rare access",
        _ => ""
    };

    partial void OnSelectedStorageTierChanged(StorageTier value)
    {
        _preview.SelectedStorageTier = value;
        OnPropertyChanged(nameof(StorageTierDescription));
    }

    public OperationPreviewViewModel(OperationPreview preview)
    {
        _preview = preview;
        _selectedStorageTier = preview.EffectiveStorageTier;

        FilesToCreate = new ObservableCollection<PreviewFileAction>(preview.FilesToCreate);
        FilesToOverwrite = new ObservableCollection<PreviewFileAction>(preview.FilesToOverwrite);
        FilesToDelete = new ObservableCollection<PreviewFileAction>(preview.FilesToDelete);
        FilesToSkip = new ObservableCollection<PreviewFileAction>(preview.FilesToSkip); // Virtualized ListBox renders only visible rows
    }

    /// <summary>
    /// Called when any file's IsIncluded state changes.
    /// Refreshes all computed properties that depend on inclusion state.
    /// </summary>
    public void RefreshInclusionState()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(TransferSize));
        OnPropertyChanged(nameof(DeleteSize));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(IncludedCreateText));
        OnPropertyChanged(nameof(IncludedOverwriteText));
        OnPropertyChanged(nameof(IncludedDeleteText));
    }

    /// <summary>
    /// Includes all files in a section.
    /// </summary>
    [RelayCommand]
    private void IncludeAllInSection(string section)
    {
        var items = GetSectionItems(section);
        foreach (var item in items)
            item.IsIncluded = true;
        RefreshInclusionState();
    }

    /// <summary>
    /// Excludes all files in a section.
    /// </summary>
    [RelayCommand]
    private void ExcludeAllInSection(string section)
    {
        var items = GetSectionItems(section);
        foreach (var item in items)
            item.IsIncluded = false;
        RefreshInclusionState();
    }

    private ObservableCollection<PreviewFileAction> GetSectionItems(string section) => section switch
    {
        "create" => FilesToCreate,
        "overwrite" => FilesToOverwrite,
        "delete" => FilesToDelete,
        _ => FilesToCreate
    };
}
