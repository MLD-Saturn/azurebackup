using AzureBackup.Core;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for a single active file row on the Progress tab.
/// Appears when a file starts downloading/uploading, disappears on success, persists on failure.
/// </summary>
public partial class ActiveFileProgressViewModel : ViewModelBase
{
    /// <summary>Index of this file in the overall operation (used for lookup).</summary>
    public int FileIndex { get; init; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long _bytesProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private FileOperationStatus _status;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Progress percentage (0–100).</summary>
    public double ProgressPercent => TotalBytes > 0
        ? (double)BytesProcessed / TotalBytes * 100
        : 0;

    /// <summary>Formatted bytes progress (e.g., "1.5 GB / 10 GB").</summary>
    public string ProgressText => TotalBytes > 0
        ? $"{FormatHelper.FormatBytes(BytesProcessed)} / {FormatHelper.FormatBytes(TotalBytes)}"
        : string.Empty;

    /// <summary>Human-readable status for display.</summary>
    public string StatusText => Status switch
    {
        FileOperationStatus.Queued => "Queued",
        FileOperationStatus.Downloading => "Downloading",
        FileOperationStatus.Uploading => "Uploading",
        FileOperationStatus.Writing => "Writing",
        FileOperationStatus.Verifying => "Verifying",
        FileOperationStatus.Complete => "Complete ✓",
        FileOperationStatus.Failed => "Failed ✗",
        FileOperationStatus.Retrying => "Retrying...",
        _ => string.Empty
    };

    /// <summary>True when the file has failed (row persists and shows error styling).</summary>
    public bool IsFailed => Status is FileOperationStatus.Failed;
}
