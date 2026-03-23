using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AzureBackup.ViewModels;

namespace AzureBackup.Views;

/// <summary>
/// Converters for drag-drop visual feedback.
/// </summary>
public static class DragDropConverters
{
    /// <summary>
    /// Converts a boolean to a Thickness for border highlighting during drag operations.
    /// </summary>
    public static readonly IValueConverter BoolToBorderThickness = 
        new FuncValueConverter<bool, Thickness>(isDragging => 
            isDragging ? new Thickness(3) : new Thickness(0));
}

public partial class SyncView : UserControl
{
    // Custom data format for internal drag operations
    private const string LocalFileDragFormat = "AzureBackup.LocalFiles";
    private const string AzureFileDragFormat = "AzureBackup.AzureFiles";
    
    private MainWindowViewModel? _viewModel;

    public SyncView()
    {
        InitializeComponent();
        
        // Wire up drag-drop events after component is initialized
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        
        // Wire up keyboard shortcuts
        KeyDown += OnKeyDown;
        
        // Subscribe to DataContext changes
        DataContextChanged += OnDataContextChanged;
    }
    
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old ViewModel
        if (_viewModel != null)
        {
            _viewModel.RemapFolderPickerRequested -= OnRemapFolderPickerRequested;
        }
        
        // Subscribe to new ViewModel
        if (DataContext is MainWindowViewModel vm)
        {
            _viewModel = vm;
            _viewModel.RemapFolderPickerRequested += OnRemapFolderPickerRequested;
        }
    }
    
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.RemapFolderPickerRequested -= OnRemapFolderPickerRequested;
        }
    }
    
    private async void OnRemapFolderPickerRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Target Folder for Path Remapping",
            AllowMultiple = false
        });
        
        if (folders.Count > 0)
        {
            var uri = folders[0].Path;
            var folderPath = uri.IsAbsoluteUri ? uri.LocalPath : uri.ToString();
            vm.SetRemapFolderPath(folderPath);
        }
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Handle keyboard shortcuts
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.B: // Ctrl+B: Backup selected local files
                    if (vm.BackupSelectedLocalFilesCommand.CanExecute(null))
                    {
                        vm.BackupSelectedLocalFilesCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.R: // Ctrl+R: Restore selected Azure files
                    if (vm.RestoreSelectedTreeFilesCommand.CanExecute(null))
                    {
                        vm.RestoreSelectedTreeFilesCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.S: // Ctrl+S: Sync selected (both backup and restore)
                    if (vm.SyncSelectedCommand.CanExecute(null))
                    {
                        vm.SyncSelectedCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.A: // Ctrl+A: Select all
                    if (vm.SelectAllSyncFilesCommand.CanExecute(null))
                    {
                        vm.SelectAllSyncFilesCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.D: // Ctrl+D: Deselect all
                    if (vm.DeselectAllSyncFilesCommand.CanExecute(null))
                    {
                        vm.DeselectAllSyncFilesCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
            }
        }
        else if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.F5: // F5: Refresh both panels
                    if (vm.RefreshBothPanelsCommand.CanExecute(null))
                    {
                        vm.RefreshBothPanelsCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Delete: // Delete: Delete selected from Azure
                    if (vm.DeleteSelectedFilesCommand.CanExecute(null))
                    {
                        vm.DeleteSelectedFilesCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
            }
        }
    }
    
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Find panels and attach drag-drop handlers
        var localPanel = this.FindControl<Border>("LocalFilesPanel");
        var azurePanel = this.FindControl<Border>("AzureFilesPanel");
        var localTreeView = this.FindControl<TreeView>("LocalFilesTreeView");
        var azureTreeView = this.FindControl<TreeView>("AzureFilesTreeView");
        
        if (localPanel != null)
        {
            localPanel.AddHandler(DragDrop.DropEvent, OnDropToLocal);
            localPanel.AddHandler(DragDrop.DragOverEvent, OnDragOverLocal);
            localPanel.AddHandler(DragDrop.DragLeaveEvent, OnDragLeaveLocal);
        }
        
        if (azurePanel != null)
        {
            azurePanel.AddHandler(DragDrop.DropEvent, OnDropToAzure);
            azurePanel.AddHandler(DragDrop.DragOverEvent, OnDragOverAzure);
            azurePanel.AddHandler(DragDrop.DragLeaveEvent, OnDragLeaveAzure);
        }
        
        // Wire up drag initiation for local files tree - use Bubble strategy to allow checkboxes to work
        if (localTreeView != null)
        {
            localTreeView.AddHandler(PointerMovedEvent, OnLocalTreePointerMoved, RoutingStrategies.Tunnel);
            localTreeView.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        }
        
        // Wire up drag initiation for Azure files tree
        if (azureTreeView != null)
        {
            azureTreeView.AddHandler(PointerMovedEvent, OnAzureTreePointerMoved, RoutingStrategies.Tunnel);
            azureTreeView.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        }
        
        // Global handler to reset drag state when pointer is released anywhere
        this.AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Tunnel);
        
        // Make the view focusable to receive keyboard events
        Focusable = true;
        Focus();
    }
    
    // Track if we're in the middle of a drag to prevent re-entry
    private bool _isDragging;
    private Point? _dragStartPoint;
    private const double DragThreshold = 5.0; // Minimum pixels to move before starting drag
    
    private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Always reset drag state when mouse is released
        _dragStartPoint = null;
        _isDragging = false;
        
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsDragOverAzurePanel = false;
            vm.IsDragOverLocalPanel = false;
            vm.DragFileCount = 0;
        }
    }
    
    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _isDragging = false;
    }
    
    private void OnLocalTreePointerMoved(object? sender, PointerEventArgs e)
    {
        // Check if left button is pressed and we're not already dragging
        if (_isDragging) return;
        
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) 
        {
            _dragStartPoint = null;
            return;
        }
        
        // Record start point if not set
        if (_dragStartPoint == null)
        {
            _dragStartPoint = point.Position;
            return;
        }
        
        // Check if we've moved enough to start a drag
        var delta = point.Position - _dragStartPoint.Value;
        if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold)
            return;
        
        // Don't start drag if the source is a checkbox
        if (e.Source is CheckBox || IsChildOfCheckBox(e.Source as Control))
            return;
            
        StartLocalFileDrag(e);
    }
    
    private void OnAzureTreePointerMoved(object? sender, PointerEventArgs e)
    {
        // Check if left button is pressed and we're not already dragging
        if (_isDragging) return;
        
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = null;
            return;
        }
        
        // Record start point if not set
        if (_dragStartPoint == null)
        {
            _dragStartPoint = point.Position;
            return;
        }
        
        // Check if we've moved enough to start a drag
        var delta = point.Position - _dragStartPoint.Value;
        if (System.Math.Abs(delta.X) < DragThreshold && System.Math.Abs(delta.Y) < DragThreshold)
            return;
        
        // Don't start drag if the source is a checkbox
        if (e.Source is CheckBox || IsChildOfCheckBox(e.Source as Control))
            return;
            
        StartAzureFileDrag(e);
    }
    
    private static bool IsChildOfCheckBox(Control? control)
    {
        while (control != null)
        {
            if (control is CheckBox) return true;
            control = control.Parent as Control;
        }
        return false;
    }
    
    
    private async void StartLocalFileDrag(PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Only start drag on left mouse button with some movement
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        
        // Find the LocalFileTreeNodeViewModel under the pointer
        var draggedItem = FindLocalFileNodeFromSource(e.Source as Control);
        
        if (draggedItem == null || !draggedItem.IsFile)
        {
            // Can't determine what's being dragged, or it's a folder - use existing selection
            var selectedFiles = vm.GetSelectedLocalFilePaths();
            if (selectedFiles.Count == 0) return;
            await PerformLocalFileDrag(e, vm, selectedFiles);
            return;
        }
        
        // Check if the dragged item is already selected
        if (draggedItem.IsSelected)
        {
            // Drag all selected items
            var selectedFiles = vm.GetSelectedLocalFilePaths();
            if (selectedFiles.Count == 0) return;
            await PerformLocalFileDrag(e, vm, selectedFiles);
        }
        else
        {
            // Dragging an unselected item - deselect all others and select just this one
            vm.DeselectAllLocalFiles();
            draggedItem.IsSelected = true;
            
            List<string> filesToDrag = new() { draggedItem.FullPath };
            await PerformLocalFileDrag(e, vm, filesToDrag);
        }
    }
    
    private async Task PerformLocalFileDrag(PointerEventArgs e, MainWindowViewModel vm, List<string> selectedFiles)
    {
        // Note: DataObject is marked obsolete in favor of DataTransfer, but DataTransfer's
        // async-only API doesn't work well with synchronous drag operations in Avalonia 11.x.
        // Using DataObject with pragma suppression until Avalonia provides a better solution.
#pragma warning disable CS0618 // DataObject is obsolete
        DataObject dataObject = new();
        dataObject.Set(LocalFileDragFormat, selectedFiles);
        
        // Also set as file paths for external drop targets
        List<IStorageItem> storageItems = new();
        foreach (var path in selectedFiles)
        {
            if (System.IO.File.Exists(path))
            {
                var file = await TopLevel.GetTopLevel(this)?.StorageProvider.TryGetFileFromPathAsync(new Uri(path))!;
                if (file != null) storageItems.Add(file);
            }
        }
        if (storageItems.Count > 0)
        {
            dataObject.Set(DataFormats.Files, storageItems);
        }
#pragma warning restore CS0618

        vm.DragFileCount = selectedFiles.Count;
        vm.DragDropPreviewText = $"Drag {selectedFiles.Count} file(s) to Azure panel to backup";
        
        _isDragging = true;
        
        try
        {
            // Perform the drag (using legacy DoDragDrop as DoDragDropAsync requires IDataTransfer)
#pragma warning disable CS0618 // DoDragDrop is obsolete
            await DragDrop.DoDragDrop(e, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
#pragma warning restore CS0618
        }
        finally
        {
            // Always clear drag state after drag completes (success, cancel, or error)
            _isDragging = false;
            _dragStartPoint = null;
            vm.IsDragOverAzurePanel = false;
            vm.IsDragOverLocalPanel = false;
            vm.DragFileCount = 0;
        }
    }
    
    private async void StartAzureFileDrag(PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Find the FileTreeNodeViewModel under the pointer
        var draggedItem = FindAzureFileNodeFromSource(e.Source as Control);
        
        if (draggedItem == null || !draggedItem.IsFile)
        {
            // Can't determine what's being dragged, or it's a folder - use existing selection
            var selectedFiles = vm.GetSelectedAzureFilePaths();
            if (selectedFiles.Count == 0) return;
            await PerformAzureFileDrag(e, vm, selectedFiles);
            return;
        }
        
        // Check if the dragged item is already selected
        if (draggedItem.IsSelected)
        {
            // Drag all selected items
            var selectedFiles = vm.GetSelectedAzureFilePaths();
            if (selectedFiles.Count == 0) return;
            await PerformAzureFileDrag(e, vm, selectedFiles);
        }
        else
        {
            // Dragging an unselected item - deselect all others and select just this one
            vm.DeselectAllAzureFiles();
            draggedItem.IsSelected = true;
            
            List<string> filesToDrag = new() { draggedItem.File!.LocalPath };
            await PerformAzureFileDrag(e, vm, filesToDrag);
        }
    }
    
    private async Task PerformAzureFileDrag(PointerEventArgs e, MainWindowViewModel vm, List<string> selectedFiles)
    {
        // Note: DataObject is marked obsolete in favor of DataTransfer, but DataTransfer's
        // async-only API doesn't work well with synchronous drag operations in Avalonia 11.x.
#pragma warning disable CS0618 // DataObject is obsolete
        DataObject dataObject = new();
        dataObject.Set(AzureFileDragFormat, selectedFiles);
#pragma warning restore CS0618
        
        vm.DragFileCount = selectedFiles.Count;
        vm.DragDropPreviewText = $"Drag {selectedFiles.Count} file(s) to Local panel to restore";
        
        _isDragging = true;
        
        try
        {
            // Perform the drag (using legacy DoDragDrop as DoDragDropAsync requires IDataTransfer)
#pragma warning disable CS0618 // DoDragDrop is obsolete
            await DragDrop.DoDragDrop(e, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
#pragma warning restore CS0618
        }
        finally
        {
            // Always clear drag state after drag completes (success, cancel, or error)
            _isDragging = false;
            _dragStartPoint = null;
            vm.IsDragOverAzurePanel = false;
            vm.IsDragOverLocalPanel = false;
            vm.DragFileCount = 0;
        }
    }
    
    /// <summary>
    /// Finds the LocalFileTreeNodeViewModel from the visual tree source.
    /// </summary>
    private static AzureBackup.ViewModels.LocalFileTreeNodeViewModel? FindLocalFileNodeFromSource(Control? source)
    {
        while (source != null)
        {
            if (source.DataContext is AzureBackup.ViewModels.LocalFileTreeNodeViewModel node)
                return node;
            source = source.Parent as Control;
        }
        return null;
    }
    
    /// <summary>
    /// Finds the FileTreeNodeViewModel from the visual tree source.
    /// </summary>
    private static AzureBackup.ViewModels.FileTreeNodeViewModel? FindAzureFileNodeFromSource(Control? source)
    {
        while (source != null)
        {
            if (source.DataContext is AzureBackup.ViewModels.FileTreeNodeViewModel node)
                return node;
            source = source.Parent as Control;
        }
        return null;
    }
    
    // Note: DragOver events must be synchronous for proper drag feedback.
    // The async DataTransfer API is not suitable here, so we use the legacy e.Data API.
    // This is an intentional design decision by Avalonia - see https://github.com/AvaloniaUI/Avalonia/issues/12567
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete but required for synchronous DragOver
    
    private void OnDragOverLocal(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Check if this is an internal Azure file drag (restore operation)
        if (e.Data.Contains(AzureFileDragFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            vm.IsDragOverLocalPanel = true;
            vm.DragDropPreviewText = $"Drop to restore {vm.DragFileCount} file(s) from Azure";
        }
        // Check for external files
        else if (e.Data.GetFiles()?.Any() == true)
        {
            e.DragEffects = DragDropEffects.None; // External files can't be "restored"
            vm.IsDragOverLocalPanel = false;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            vm.IsDragOverLocalPanel = false;
        }
    }
    
    private void OnDragLeaveLocal(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsDragOverLocalPanel = false;
        }
    }
    
    private void OnDragOverAzure(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Check if this is an internal local file drag (backup operation)
        if (e.Data.Contains(LocalFileDragFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            vm.IsDragOverAzurePanel = true;
            vm.DragDropPreviewText = $"Drop to backup {vm.DragFileCount} file(s) to Azure";
        }
        // Check for external files (backup from explorer)
        else if (e.Data.GetFiles()?.Any() == true)
        {
            var files = e.Data.GetFiles()!;
            var count = files.Count();
            e.DragEffects = DragDropEffects.Copy;
            vm.IsDragOverAzurePanel = true;
            vm.DragFileCount = count;
            vm.DragDropPreviewText = $"Drop to backup {count} file(s) to Azure";
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            vm.IsDragOverAzurePanel = false;
        }
    }
    
    private void OnDragLeaveAzure(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsDragOverAzurePanel = false;
        }
    }
    
#pragma warning restore CS0618 // End of synchronous DragOver section
    
    private async void OnDropToLocal(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Clear visual state
        vm.IsDragOverLocalPanel = false;
        
        // Note: Using e.Data (legacy API) because e.DataTransfer async methods
        // don't have the expected ContainsAsync/GetDataAsync in Avalonia 11.x
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
        
        // Handle internal Azure file drag (restore operation)
        if (e.Data.Contains(AzureFileDragFormat))
        {
            var filePaths = e.Data.Get(AzureFileDragFormat) as List<string>;
            if (filePaths != null && filePaths.Count > 0)
            {
                vm.AddLogMessage($"Restoring {filePaths.Count} file(s) from Azure...");
                
                // Trigger restore for selected Azure files
                if (vm.RestoreSelectedTreeFilesCommand.CanExecute(null))
                {
                    await vm.RestoreSelectedTreeFilesCommand.ExecuteAsync(null);
                }
            }
            return;
        }
        
        // External files dropped to local panel - not a valid operation
        var files = e.Data.GetFiles();
        if (files != null && files.Any())
        {
            vm.AddLogMessage("Note: To restore files from Azure, drag from the Azure panel (right) to here.");
        }
#pragma warning restore CS0618
    }
    
    private async void OnDropToAzure(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Clear visual state
        vm.IsDragOverAzurePanel = false;
        
        // Check if app is initialized
        if (!vm.IsInitialized)
        {
            vm.AddLogMessage("Please unlock the application first before backing up files.");
            return;
        }
        
        // Note: Using e.Data (legacy API) because e.DataTransfer async methods
        // don't have the expected ContainsAsync/GetDataAsync in Avalonia 11.x
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
        
        // Handle internal local file drag (backup operation)
        if (e.Data.Contains(LocalFileDragFormat))
        {
            var filePaths = e.Data.Get(LocalFileDragFormat) as List<string>;
            if (filePaths != null && filePaths.Count > 0)
            {
                vm.AddLogMessage($"Backing up {filePaths.Count} file(s) to Azure...");
                await vm.BackupFilePathsAsync(filePaths);
            }
            return;
        }
        
        // Handle external files (drag from File Explorer)
        var files = e.Data.GetFiles();
        if (files == null) return;
        
        List<string> externalFilePaths = new();
        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (path == null) continue;
            
            if (System.IO.File.Exists(path))
            {
                externalFilePaths.Add(path);
            }
            else if (System.IO.Directory.Exists(path))
            {
                // Add all files from directory
                try
                {
                    var dirFiles = System.IO.Directory.EnumerateFiles(
                        path, "*", 
                        System.IO.SearchOption.AllDirectories);
                    externalFilePaths.AddRange(dirFiles);
                }
                catch (Exception ex)
                {
                    vm.AddLogMessage($"Could not read directory: {ex.Message}");
                }
            }
        }
#pragma warning restore CS0618
        
        if (externalFilePaths.Count > 0)
        {
            vm.AddLogMessage($"Backing up {externalFilePaths.Count} external file(s) to Azure...");
            await vm.BackupFilePathsAsync(externalFilePaths);
        }
    }
}
