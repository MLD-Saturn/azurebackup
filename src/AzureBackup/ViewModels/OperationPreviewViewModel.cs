using System.Collections.ObjectModel;
using System.Linq;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

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
    /// Summary text for the operation.
    /// </summary>
    public string Summary
    {
        get
        {
            System.Collections.Generic.List<string> parts = new();
            
            if (_preview.CreateCount > 0)
                parts.Add($"{_preview.CreateCount} new");
            if (_preview.OverwriteCount > 0)
                parts.Add($"{_preview.OverwriteCount} overwrite");
            if (_preview.DeleteCount > 0)
                parts.Add($"{_preview.DeleteCount} delete");
            if (_preview.SkipCount > 0)
                parts.Add($"{_preview.SkipCount} unchanged");

            return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
        }
    }

    /// <summary>
    /// Total bytes to transfer formatted.
    /// </summary>
    public string TransferSize => FormatBytes(_preview.TotalBytesToTransfer);

    /// <summary>
    /// Total bytes to delete formatted.
    /// </summary>
    public string DeleteSize => FormatBytes(_preview.TotalBytesToDelete);

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
    /// Whether the confirm button should be enabled.
    /// </summary>
    public bool CanConfirm => _preview.HasChanges;

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

    public OperationPreviewViewModel(OperationPreview preview)
    {
        _preview = preview;
        
        FilesToCreate = new ObservableCollection<PreviewFileAction>(preview.FilesToCreate);
        FilesToOverwrite = new ObservableCollection<PreviewFileAction>(preview.FilesToOverwrite);
        FilesToDelete = new ObservableCollection<PreviewFileAction>(preview.FilesToDelete);
        FilesToSkip = new ObservableCollection<PreviewFileAction>(preview.FilesToSkip.Take(100)); // Limit skipped files shown
    }

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
