using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AzureBackup.ViewModels;

namespace AzureBackup.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _currentViewModel;
    private bool _startupCompleted;

    public MainWindow()
    {
        InitializeComponent();
        
        // Wire up picker requests from the ViewModel
        DataContextChanged += OnDataContextChanged;
        Closed += OnWindowClosed;
        
        // Wire up window activation for auto-refresh
        Activated += OnWindowActivated;
        
        // Handle startup flow when window opens
        Opened += OnWindowOpened;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_startupCompleted) return;
        _startupCompleted = true;
        
        await HandleStartupFlowAsync();
    }

    private async Task HandleStartupFlowAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // If not configured (new user), go to Settings
        if (vm.NeedsConfiguration)
        {
            vm.CurrentView = "Settings";
            return;
        }
        
        // If configured but locked (including migration case), show password dialog
        if (vm.NeedsUnlock)
        {
            var unlocked = await ShowPasswordDialogAsync(vm);
            
            if (unlocked)
            {
                // Successfully unlocked - go to Sync
                vm.CurrentView = "Sync";
            }
            else
            {
                // User cancelled or failed - go to Settings so they can see status
                vm.CurrentView = "Settings";
            }
        }
        else
        {
            // Already initialized somehow - go to Sync
            vm.CurrentView = "Sync";
        }
    }

    private async Task<bool> ShowPasswordDialogAsync(MainWindowViewModel vm)
    {
        const int maxAttempts = 3;
        var attempts = 0;
        
        while (attempts < maxAttempts)
        {
            PasswordDialog dialog = new();
            var result = await dialog.ShowDialog<bool?>(this);
            
            if (result != true)
            {
                // User cancelled
                return false;
            }
            
            var password = dialog.Password;
            var (success, errorMessage) = await vm.TryUnlockWithPasswordAsync(password);
            
            if (success)
            {
                return true;
            }
            
            attempts++;
            
            if (attempts < maxAttempts)
            {
                // Show error in a new dialog
                PasswordDialog retryDialog = new();
                retryDialog.ShowError($"{errorMessage} ({maxAttempts - attempts} attempts remaining)");
                
                var retryResult = await retryDialog.ShowDialog<bool?>(this);
                
                if (retryResult != true)
                {
                    return false;
                }
                
                password = retryDialog.Password;
                (success, errorMessage) = await vm.TryUnlockWithPasswordAsync(password);
                
                if (success)
                {
                    return true;
                }
                
                attempts++;
            }
        }
        
        // Max attempts reached
        vm.AddLogMessage("Maximum password attempts reached. Please try again later.");
        return false;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old ViewModel
        if (_currentViewModel != null)
        {
            _currentViewModel.FolderPickerRequested -= OnFolderPickerRequested;
            _currentViewModel.RestoreFolderPickerRequested -= OnRestoreFolderPickerRequested;
            _currentViewModel.FilePickerRequested -= OnFilePickerRequested;
            _currentViewModel.PreviewDialogRequested -= OnPreviewDialogRequested;
        }

        // Subscribe to new ViewModel
        if (DataContext is MainWindowViewModel vm)
        {
            _currentViewModel = vm;
            vm.FolderPickerRequested += OnFolderPickerRequested;
            vm.RestoreFolderPickerRequested += OnRestoreFolderPickerRequested;
            vm.FilePickerRequested += OnFilePickerRequested;
            vm.PreviewDialogRequested += OnPreviewDialogRequested;
        }
        else
        {
            _currentViewModel = null;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Clean up event subscriptions when window closes
        if (_currentViewModel != null)
        {
            _currentViewModel.FolderPickerRequested -= OnFolderPickerRequested;
            _currentViewModel.RestoreFolderPickerRequested -= OnRestoreFolderPickerRequested;
            _currentViewModel.FilePickerRequested -= OnFilePickerRequested;
            _currentViewModel.PreviewDialogRequested -= OnPreviewDialogRequested;
            _currentViewModel = null;
        }
    }

    private async void OnWindowActivated(object? sender, EventArgs e)
    {
        // Auto-refresh Sync view when window gains focus
        if (_currentViewModel != null)
        {
            try
            {
                await _currentViewModel.OnWindowActivatedAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during window activation refresh: {ex}");
            }
        }
    }

    private async void OnFolderPickerRequested(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Folder to Watch",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                // Handle both local paths and URI paths
                var folder = folders[0];
                var folderPath = folder.TryGetLocalPath() ?? folder.Path.ToString();
                vm.AddWatchedFolderPath(folderPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in folder picker: {ex}");
            if (DataContext is MainWindowViewModel vm)
            {
                vm.AddLogMessage($"Error selecting folder: {ex.Message}");
            }
        }
    }

    private async void OnRestoreFolderPickerRequested(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Restore Destination",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                // Handle both local paths and URI paths
                var folder = folders[0];
                var folderPath = folder.TryGetLocalPath() ?? folder.Path.ToString();
                vm.SetRestoreDirectory(folderPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in restore folder picker: {ex}");
            if (DataContext is MainWindowViewModel vm)
            {
                vm.AddLogMessage($"Error selecting restore folder: {ex.Message}");
            }
        }
    }

    private async void OnFilePickerRequested(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Files to Backup",
                AllowMultiple = true
            });

            if (files.Count > 0)
            {
                // Handle both local paths and URI paths
                var filePaths = files
                    .Select(f => f.TryGetLocalPath() ?? f.Path.ToString())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();
                
                if (filePaths.Length > 0)
                {
                    await vm.BackupSelectedFilesAsync(filePaths);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in file picker: {ex}");
            if (DataContext is MainWindowViewModel vm)
            {
                vm.AddLogMessage($"Error selecting files: {ex.Message}");
            }
        }
    }

    private async Task<bool> OnPreviewDialogRequested(OperationPreviewViewModel previewViewModel)
    {
        try
        {
            PreviewDialog dialog = new() { DataContext = previewViewModel };
            var result = await dialog.ShowDialog<bool?>(this);
            return result == true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing preview dialog: {ex}");
            // On error, default to not proceeding (safer)
            return false;
        }
    }
}