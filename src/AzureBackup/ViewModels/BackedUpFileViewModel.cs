using System;
using System;
using System.IO;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel wrapper for a backed-up file, providing formatted display properties.
/// </summary>
public partial class BackedUpFileViewModel : ObservableObject
{
    /// <summary>
    /// The underlying model.
    /// </summary>
    public BackedUpFile Model { get; }

    /// <summary>
    /// Full local path of the file.
    /// </summary>
    public string LocalPath => Model.LocalPath;

    /// <summary>
    /// File name without directory path.
    /// </summary>
    public string FileName => Path.GetFileName(Model.LocalPath);

    /// <summary>
    /// Directory containing the file.
    /// </summary>
    public string Directory => Path.GetDirectoryName(Model.LocalPath) ?? string.Empty;

    /// <summary>
    /// Display path showing abbreviated directory.
    /// </summary>
    public string DisplayPath
    {
        get
        {
            var dir = Directory;
            var name = FileName;
            if (string.IsNullOrEmpty(dir))
                return name;
            
            // Abbreviate long paths
            if (dir.Length > 40)
            {
                var parts = dir.Split(Path.DirectorySeparatorChar);
                if (parts.Length > 3)
                {
                    dir = $"{parts[0]}\\...\\{parts[^2]}\\{parts[^1]}";
                }
            }
            return $"{dir}{Path.DirectorySeparatorChar}{name}";
        }
    }

    /// <summary>
    /// Human-readable file size (e.g., "1.5 MB").
    /// </summary>
    public string FileSize => FormatBytes(Model.FileSize);

    /// <summary>
    /// Human-readable file size (alias for FileSize for binding compatibility).
    /// </summary>
    public string FileSizeText => FileSize;

    /// <summary>
    /// Last modified date/time formatted for display.
    /// </summary>
    public string LastModified => Model.LastModified.ToString("g");

    /// <summary>
    /// Backup status as string.
    /// </summary>
    public string Status => Model.Status.ToString();

    /// <summary>
    /// Whether this item is selected in the UI.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Current storage tier of this file in Azure.
    /// </summary>
    public StorageTier? StorageTier => Model.CurrentStorageTier;

    /// <summary>
    /// Storage tier display text.
    /// </summary>
    public string StorageTierText => Model.CurrentStorageTier?.ToString() ?? "Unknown";

    /// <summary>
    /// Whether the storage tier is known.
    /// </summary>
    public bool HasStorageTier => Model.CurrentStorageTier.HasValue;

    public BackedUpFileViewModel(BackedUpFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
    }

    /// <summary>
    /// Formats bytes into human-readable size string.
    /// </summary>
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
