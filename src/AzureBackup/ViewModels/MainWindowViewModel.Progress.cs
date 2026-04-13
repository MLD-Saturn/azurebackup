using System;

namespace AzureBackup.ViewModels;

/// <summary>
/// Progress tracking helpers for file transfer operations.
/// </summary>
public partial class MainWindowViewModel
{
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
    /// Used by sequential operations (restore) where files complete one at a time.
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
    /// Updates overall progress using a pre-computed aggregate byte total.
    /// Used by parallel operations where the service layer tracks aggregate bytes via Interlocked.
    /// Avoids the single-file recomputation that causes jumps during parallel processing.
    /// </summary>
    private void UpdateOverallProgress(long aggregateBytesProcessed, int completedFiles)
    {
        TotalBytesProcessed = aggregateBytesProcessed;
        CompletedFilesCount = completedFiles;
        ProgressValue = TotalBytesToProcess > 0
            ? (double)TotalBytesProcessed / TotalBytesToProcess * 100
            : 0;

        OnPropertyChanged(nameof(BytesProgressText));
        OnPropertyChanged(nameof(FilesProgressText));
        UpdateSpeedAndEta();
    }

    /// <summary>
    /// Updates the current file display (name, per-file progress bar) without modifying
    /// overall byte totals. Used alongside UpdateOverallProgress for parallel operations.
    /// </summary>
    private void UpdateCurrentFileDisplay(string fileName, long bytesProcessed, long fileSize, int fileIndex)
    {
        CurrentFileName = fileName;
        CurrentFileProgress = fileSize > 0 ? (double)bytesProcessed / fileSize * 100 : 0;
        CurrentFileProgressText = $"{AzureBackup.Core.FormatHelper.FormatBytes(bytesProcessed)} / {AzureBackup.Core.FormatHelper.FormatBytes(fileSize)}";
        _currentFileIndex = fileIndex;
        ProgressText = $"{CurrentOperationType}: {fileName} ({fileIndex + 1}/{TotalFilesInOperation})";
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
}
