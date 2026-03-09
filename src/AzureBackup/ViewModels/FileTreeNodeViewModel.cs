using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for a file tree node, supporting hierarchical selection and path remapping.
/// </summary>
public partial class FileTreeNodeViewModel : ObservableObject
{
    private readonly FileTreeNode _model;
    private FileTreeNodeViewModel? _parent;
    private bool _isUpdatingSelection;

    /// <summary>
    /// The underlying model.
    /// </summary>
    public FileTreeNode Model => _model;

    /// <summary>
    /// Name of this node (file or folder name).
    /// </summary>
    public string Name => _model.Name;

    /// <summary>
    /// Full original path.
    /// </summary>
    public string FullPath => _model.FullPath;

    /// <summary>
    /// True if this is a folder node.
    /// </summary>
    public bool IsFolder => _model.IsFolder;

    /// <summary>
    /// True if this is a file node.
    /// </summary>
    public bool IsFile => !_model.IsFolder;

    /// <summary>
    /// The backed up file (null for folders).
    /// </summary>
    public BackedUpFile? File => _model.File;

    /// <summary>
    /// Storage tier of this file (null for folders or if unknown).
    /// </summary>
    public StorageTier? StorageTier => _model.File?.CurrentStorageTier;

    /// <summary>
    /// Storage tier display text.
    /// </summary>
    public string StorageTierText => _model.File?.CurrentStorageTier?.ToString() ?? "";

    /// <summary>
    /// Whether the storage tier is known (for UI visibility).
    /// </summary>
    public bool HasStorageTier => _model.File?.CurrentStorageTier.HasValue == true;

    /// <summary>
    /// Child nodes.
    /// </summary>
    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = [];

    /// <summary>
    /// Parent node (null for root).
    /// </summary>
    public FileTreeNodeViewModel? Parent
    {
        get => _parent;
        set => SetProperty(ref _parent, value);
    }

    /// <summary>
    /// Whether this node is expanded in the tree view.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Whether this node is selected (checked).
    /// When a folder is selected, all children are also selected.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPartiallySelected))]
    private bool _isSelected;

    /// <summary>
    /// True if this folder has some but not all children selected.
    /// Used for showing indeterminate checkbox state.
    /// </summary>
    public bool IsPartiallySelected
    {
        get
        {
            if (!IsFolder || Children.Count == 0)
                return false;

            var selectedCount = Children.Count(c => c.IsSelected || c.IsPartiallySelected);
            return selectedCount > 0 && selectedCount < Children.Count;
        }
    }

    /// <summary>
    /// Custom restore path override for this node.
    /// When set, this path replaces the original path during restore.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRestorePath))]
    [NotifyPropertyChangedFor(nameof(HasCustomRestorePath))]
    private string? _customRestorePath;

    /// <summary>
    /// True if this node has a custom restore path set.
    /// </summary>
    public bool HasCustomRestorePath => !string.IsNullOrEmpty(CustomRestorePath);

    /// <summary>
    /// The effective restore path, considering parent overrides.
    /// </summary>
    public string EffectiveRestorePath
    {
        get
        {
            // If this node has a custom path, use it
            if (HasCustomRestorePath)
                return CustomRestorePath!;

            // Check if any parent has a custom path
            var current = Parent;
            while (current != null)
            {
                if (current.HasCustomRestorePath)
                {
                    // Remap: replace parent's original path with custom path
                    var relativePath = Path.GetRelativePath(current.FullPath, FullPath);
                    return Path.Combine(current.CustomRestorePath!, relativePath);
                }
                current = current.Parent;
            }

            // No override, use original path
            return FullPath;
        }
    }

    /// <summary>
    /// Display string showing aggregate info for folders.
    /// </summary>
    public string AggregateInfo
    {
        get
        {
            if (!IsFolder)
                return FormatBytes(_model.File?.FileSize ?? 0);

            var fileCount = _model.FileCount;
            var folderCount = _model.FolderCount;
            var totalSize = _model.TotalSize;

            List<string> parts = new();
            if (fileCount > 0)
                parts.Add($"{fileCount} file{(fileCount != 1 ? "s" : "")}");
            if (folderCount > 0)
                parts.Add($"{folderCount} folder{(folderCount != 1 ? "s" : "")}");
            parts.Add(FormatBytes(totalSize));

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// File size formatted for display.
    /// </summary>
    public string FileSizeText => IsFile ? FormatBytes(_model.File?.FileSize ?? 0) : string.Empty;

    /// <summary>
    /// Last modified date for files.
    /// </summary>
    public string LastModifiedText => IsFile && _model.File != null 
        ? _model.File.LastModified.ToString("g") 
        : string.Empty;

    /// <summary>
    /// Icon indicator based on node type.
    /// </summary>
    public string Icon => IsFolder ? "[Dir]" : "[Cloud]";

    public FileTreeNodeViewModel(FileTreeNode model, FileTreeNodeViewModel? parent = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _parent = parent;

        // Build children
        foreach (var childModel in model.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name))
        {
            FileTreeNodeViewModel childVm = new(childModel, this);
            Children.Add(childVm);
        }
    }

    /// <summary>
    /// Called when IsSelected changes. Propagates selection to children and updates parent.
    /// </summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (_isUpdatingSelection)
            return;

        _isUpdatingSelection = true;
        try
        {
            // If this is a folder, select/deselect all children
            if (IsFolder)
            {
                foreach (var child in Children)
                {
                    child.SetSelectionRecursive(value);
                }
            }

            // Update parent's partial selection state
            Parent?.OnChildSelectionChanged();
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    /// <summary>
    /// Called when IsExpanded changes. Auto-expands single-child folder chains
    /// to improve navigation UX.
    /// </summary>
    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || !IsFolder)
            return;

        // Auto-expand if this folder has exactly one child and it's a folder
        // This continues recursively for chains like: Users > Username > Documents
        if (Children.Count == 1 && Children[0].IsFolder)
        {
            Children[0].IsExpanded = true;
        }
    }

    /// <summary>
    /// Sets selection state recursively without triggering parent updates.
    /// </summary>
    private void SetSelectionRecursive(bool selected)
    {
        _isUpdatingSelection = true;
        try
        {
            IsSelected = selected;
            foreach (var child in Children)
            {
                child.SetSelectionRecursive(selected);
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    /// <summary>
    /// Called when a child's selection changes.
    /// </summary>
    private void OnChildSelectionChanged()
    {
        OnPropertyChanged(nameof(IsPartiallySelected));
        
        // Update our selection state based on children
        _isUpdatingSelection = true;
        try
        {
            var allSelected = Children.All(c => c.IsSelected);
            var noneSelected = Children.All(c => !c.IsSelected && !c.IsPartiallySelected);
            
            if (allSelected)
                IsSelected = true;
            else if (noneSelected)
                IsSelected = false;
            // Otherwise keep current state (partial)
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        // Propagate up
        Parent?.OnChildSelectionChanged();
    }

    /// <summary>
    /// Gets all selected file nodes in this subtree.
    /// </summary>
    public IEnumerable<FileTreeNodeViewModel> GetSelectedFiles()
    {
        if (IsFile && IsSelected)
        {
            yield return this;
        }

        foreach (var child in Children)
        {
            foreach (var selected in child.GetSelectedFiles())
            {
                yield return selected;
            }
        }
    }

    /// <summary>
    /// Gets all nodes in this subtree.
    /// </summary>
    public IEnumerable<FileTreeNodeViewModel> GetAllDescendants()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.GetAllDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Sets custom restore path for this node and notifies all descendants.
    /// </summary>
    public void SetCustomRestorePathAndNotify(string? path)
    {
        CustomRestorePath = path;
        
        // Notify all descendants that their effective path may have changed
        foreach (var descendant in GetAllDescendants().Skip(1))
        {
            descendant.OnPropertyChanged(nameof(EffectiveRestorePath));
        }
    }

    /// <summary>
    /// Clears custom restore path from this node and all descendants.
    /// </summary>
    public void ClearCustomRestorePathRecursive()
    {
        CustomRestorePath = null;
        foreach (var child in Children)
        {
            child.ClearCustomRestorePathRecursive();
        }
    }

    /// <summary>
    /// Expands this node and all ancestors.
    /// </summary>
    public void ExpandToRoot()
    {
        IsExpanded = true;
        Parent?.ExpandToRoot();
    }

    /// <summary>
    /// Expands all nodes in this subtree.
    /// </summary>
    public void ExpandAll()
    {
        IsExpanded = true;
        foreach (var child in Children)
        {
            child.ExpandAll();
        }
    }

    /// <summary>
    /// Collapses all nodes in this subtree.
    /// </summary>
    public void CollapseAll()
    {
        IsExpanded = false;
        foreach (var child in Children)
        {
            child.CollapseAll();
        }
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

    /// <summary>
    /// Builds a tree structure from a flat list of backed up files.
    /// </summary>
    public static List<FileTreeNodeViewModel> BuildTree(IEnumerable<BackedUpFile> files)
    {
        Dictionary<string, FileTreeNode> rootNodes = new();

        foreach (var file in files)
        {
            var pathParts = file.LocalPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            
            // Find or create root (drive letter or first path component)
            var rootName = pathParts[0];
            if (!rootName.EndsWith(':'))
                rootName = pathParts[0]; // Unix-style root
            else
                rootName = rootName + Path.DirectorySeparatorChar; // Windows drive like "C:\"

            if (!rootNodes.TryGetValue(rootName, out var rootNode))
            {
                rootNode = new FileTreeNode
                {
                    Name = rootName,
                    FullPath = rootName,
                    IsFolder = true
                };
                rootNodes[rootName] = rootNode;
            }

            // Build path down to the file
            var currentNode = rootNode;
            var currentPath = rootName;

            for (var i = 1; i < pathParts.Length; i++)
            {
                var part = pathParts[i];
                if (string.IsNullOrEmpty(part))
                    continue;

                currentPath = Path.Combine(currentPath, part);
                var isLastPart = i == pathParts.Length - 1;

                var existingChild = currentNode.Children.FirstOrDefault(c => 
                    c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

                if (existingChild != null)
                {
                    currentNode = existingChild;
                }
                else
                {
                    FileTreeNode newNode = new()
                    {
                        Name = part,
                        FullPath = currentPath,
                        IsFolder = !isLastPart,
                        File = isLastPart ? file : null,
                        Parent = currentNode
                    };
                    currentNode.Children.Add(newNode);
                    currentNode = newNode;
                }
            }
        }

        // Convert to ViewModels
        return rootNodes.Values
            .OrderBy(n => n.Name)
            .Select(n => new FileTreeNodeViewModel(n))
            .ToList();
    }
}
