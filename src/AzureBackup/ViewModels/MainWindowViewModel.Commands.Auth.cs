using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Authentication and settings commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Authentication Method Selection

    [RelayCommand]
    private void SelectEntraIdAuth()
    {
        UseEntraIdAuth = true;
        AddLog("Switched to Microsoft Entra ID authentication (for work/school accounts)");
    }

    [RelayCommand]
    private void SelectConnectionStringAuth()
    {
        UseEntraIdAuth = false;
        AddLog("Switched to Connection String authentication (for personal accounts)");
    }

    [RelayCommand]
    private void ToggleDiagnosticLogging()
    {
        EnableDiagnosticLogging = !EnableDiagnosticLogging;
        AddLog(EnableDiagnosticLogging 
            ? "?? Diagnostic logging ENABLED - detailed service logs will be shown" 
            : "Diagnostic logging disabled");
    }

    #endregion

    #region Entra ID Authentication Commands

    private CancellationTokenSource? _signInCts;

    [RelayCommand]
    private async Task SignInWithEntraIdAsync()
    {
        if (IsOperationInProgress)
        {
            AddLog("Sign-in already in progress...");
            return;
        }

        IsOperationInProgress = true;
        _signInCts = new CancellationTokenSource();
        
        try
        {
            AddLog("Opening browser for Microsoft sign-in... (timeout: 2 minutes)");
            EntraIdStatus = "Waiting for browser sign-in...";
            
            var (success, message) = await _orchestrator.AuthenticateWithEntraIdAsync(_signInCts.Token);
            
            if (success)
            {
                IsEntraIdAuthenticated = true;
                EntraIdStatus = "Signed in successfully!";
                AddLog(message);
            }
            else
            {
                IsEntraIdAuthenticated = false;
                EntraIdStatus = "Not signed in";
                AddLog(message);
            }
        }
        catch (System.Exception ex)
        {
            IsEntraIdAuthenticated = false;
            EntraIdStatus = "Not signed in";
            AddLog($"Sign-in error: {ex.Message}");
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        if (_signInCts != null && !_signInCts.IsCancellationRequested)
        {
            AddLog("Cancelling sign-in...");
            _signInCts.Cancel();
            EntraIdStatus = "Sign-in cancelled";
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsOperationInProgress = true;
        try
        {
            if (UseEntraIdAuth)
            {
                // Entra ID authentication
                if (string.IsNullOrWhiteSpace(StorageAccountName))
                {
                    AddLog("Please enter a storage account name");
                    return;
                }

                if (!_orchestrator.IsEntraIdAuthenticated)
                {
                    AddLog("Please sign in with Microsoft Entra ID first");
                    return;
                }

                var (success, message) = await _orchestrator.TestAzureConnectionAsync(StorageAccountName, ContainerName);
                AddLog(success ? $"? {message}" : $"? {message}");
            }
            else
            {
                // Connection String authentication
                if (string.IsNullOrWhiteSpace(ConnectionString) || ConnectionString.StartsWith("[Encrypted"))
                {
                    AddLog("Please enter a connection string");
                    return;
                }

                var (success, message) = await _orchestrator.TestConnectionStringAsync(ConnectionString, ContainerName);
                AddLog(success ? $"? {message}" : $"? {message}");
            }
        }
        catch (System.Exception ex)
        {
            AddLog($"? Connection test failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task SaveStorageSettingsAsync()
    {
        IsOperationInProgress = true;
        try
        {
            if (UseEntraIdAuth)
            {
                // Entra ID authentication
                if (string.IsNullOrWhiteSpace(StorageAccountName))
                {
                    AddLog("Please enter a storage account name");
                    return;
                }

                if (!_orchestrator.IsEntraIdAuthenticated)
                {
                    AddLog("Please sign in with Microsoft Entra ID first");
                    return;
                }

                await _orchestrator.SaveStorageAccountAsync(StorageAccountName, ContainerName);
                AddLog("Entra ID settings saved and connected!");
            }
            else
            {
                // Connection String authentication
                if (string.IsNullOrWhiteSpace(ConnectionString) || ConnectionString.StartsWith("[Encrypted"))
                {
                    AddLog("Please enter a connection string");
                    return;
                }

                if (!IsInitialized)
                {
                    AddLog("Please unlock the application first (enter password and click Initialize)");
                    return;
                }

                await _orchestrator.SaveConnectionStringAsync(ConnectionString, ContainerName);
                ConnectionString = "[Encrypted - stored securely]";
                AddLog("Connection string saved (encrypted) and connected!");
            }
            
            // Save watched folders and budget settings
            var config = _databaseService.GetConfiguration();
            config.WatchedFolders = WatchedFolders.Select(f => f.ToModel()).ToList();
            _databaseService.SaveConfiguration(config);
        }
        catch (System.Exception ex)
        {
            AddLog($"Error saving settings: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var config = _databaseService.GetConfiguration();
            config.ContainerName = ContainerName;
            config.StorageAccountName = StorageAccountName;
            config.WatchedFolders = WatchedFolders.Select(f => f.ToModel()).ToList();
            _databaseService.SaveConfiguration(config);

            AddLog("Settings saved");
        }
        catch (System.Exception ex)
        {
            AddLog($"Error saving settings: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex}");
        }
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            AddLog("Please enter a password");
            return;
        }

        // For new setup or migration, require password confirmation
        if (!HasExistingConfig || _needsMigration)
        {
            if (string.IsNullOrWhiteSpace(PasswordConfirm))
            {
                AddLog("Please confirm your password");
                return;
            }
            
            if (Password != PasswordConfirm)
            {
                AddLog("Passwords do not match");
                return;
            }
        }

        IsOperationInProgress = true;
        try
        {
            // Step 1: Handle migration from unencrypted database if needed
            if (_needsMigration)
            {
                AddLog("Migrating database to encrypted format...");
                var tempPath = AppMode.DatabasePath + ".encrypted";
                
                try
                {
                    // Migrate to encrypted format
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, Password);
                    
                    // Close any existing connections and swap files
                    _databaseService.Close();
                    
                    // Backup old database and replace with encrypted one
                    var backupPath = AppMode.DatabasePath + ".unencrypted.bak";
                    File.Move(AppMode.DatabasePath, backupPath);
                    File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return;
                }
            }
            
            // Step 2: Handle migration from legacy encrypted database (raw password) to Argon2id
            if (_needsLegacyMigration)
            {
                AddLog("Upgrading database encryption to Argon2id...");
                var tempPath = AppMode.DatabasePath + ".upgraded";
                
                try
                {
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, Password);
                    _databaseService.Close();
                    
                    var backupPath = AppMode.DatabasePath + ".legacy.bak";
                    File.Move(AppMode.DatabasePath, backupPath);
                    File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database encryption upgraded to Argon2id successfully");
                    _needsLegacyMigration = false;
                }
                catch (AzureBackup.Core.InvalidPasswordException)
                {
                    AddLog("Invalid password - please try again");
                    return;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Encryption upgrade failed: {ex.Message}");
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return;
                }
            }

            // Step 3: Initialize the encrypted database with password
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, Password);
                AddLog("Database unlocked successfully");
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                AddLog("Invalid password - please try again");
                return;
            }
            
            // Step 4: Load configuration from the now-unlocked database
            LoadConfiguration();
            
            // Step 5: Initialize encryption service for backup operations
            var success = await _orchestrator.InitializeAsync(Password);
            if (success)
            {
                IsInitialized = true;
                AddLog("Encryption initialized successfully!");
                
                // Clear sensitive data from UI memory
                Password = string.Empty;
                PasswordConfirm = string.Empty;
                
                // Update Entra ID status
                IsEntraIdAuthenticated = _orchestrator.IsEntraIdAuthenticated;
                
                // Check if Azure storage is configured (either Entra ID or connection string)
                var config = _databaseService.GetConfiguration();
                var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(config.StorageAccountName);
                var hasConnectionStringConfig = !UseEntraIdAuth && config.EncryptedConnectionString != null;
                
                if (hasEntraIdConfig || hasConnectionStringConfig)
                {
                    AddLog("Loading files from Azure...");
                    await RefreshFromAzureAsync();
                }
                else
                {
                    if (UseEntraIdAuth)
                    {
                        AddLog("Please sign in with Microsoft and configure storage account in Settings.");
                    }
                    else
                    {
                        AddLog("Please configure your Azure connection string in Settings.");
                    }
                }
                
                // Refresh local files (watched folders)
                await RefreshLocalFilesAsync();
            }
            else
            {
                AddLog("Failed to initialize encryption");
            }
        }
        catch (AzureBackup.Core.SecurityPolicyException ex)
        {
            AddLog($"Security: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            AddLog($"Initialization failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Combined command that handles initialization, storage settings, and connection in one step.
    /// </summary>
    [RelayCommand]
    private async Task UnlockAndConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            AddLog("Please enter a password");
            return;
        }

        var isNewSetup = !HasExistingConfig;
        
        // Save user input before LoadConfiguration potentially overwrites it
        // This is needed for new setups where config is empty
        var userConnectionString = ConnectionString;
        var userContainerName = ContainerName;
        var userStorageAccountName = StorageAccountName;

        // For new setup or migration, require password confirmation
        if (isNewSetup || _needsMigration)
        {
            if (string.IsNullOrWhiteSpace(PasswordConfirm))
            {
                AddLog("Please confirm your password");
                return;
            }
            
            if (Password != PasswordConfirm)
            {
                AddLog("Passwords do not match");
                return;
            }

            // Validate storage configuration for new users (not for migration)
            if (isNewSetup && !_needsMigration)
            {
                if (UseEntraIdAuth)
                {
                    if (string.IsNullOrWhiteSpace(userStorageAccountName))
                    {
                        AddLog("Please enter a storage account name");
                        return;
                    }
                    if (!_orchestrator.IsEntraIdAuthenticated)
                    {
                        AddLog("Please sign in with Microsoft Entra ID first");
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(userConnectionString) || userConnectionString.StartsWith("[Encrypted"))
                    {
                        AddLog("Please enter a connection string");
                        return;
                    }
                }
            }
        }

        IsOperationInProgress = true;
        try
        {
            // Step 1: Handle migration from unencrypted database if needed
            if (_needsMigration)
            {
                AddLog("Migrating database to encrypted format...");
                var tempPath = AppMode.DatabasePath + ".encrypted";
                
                try
                {
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, Password);
                    _databaseService.Close();
                    
                    var backupPath = AppMode.DatabasePath + ".unencrypted.bak";
                    System.IO.File.Move(AppMode.DatabasePath, backupPath);
                    System.IO.File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return;
                }
            }
            
            // Step 1b: Handle migration from legacy encrypted database to Argon2id
            if (_needsLegacyMigration)
            {
                AddLog("Upgrading database encryption to Argon2id...");
                var tempPath = AppMode.DatabasePath + ".upgraded";
                
                try
                {
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, Password);
                    _databaseService.Close();
                    
                    var backupPath = AppMode.DatabasePath + ".legacy.bak";
                    System.IO.File.Move(AppMode.DatabasePath, backupPath);
                    System.IO.File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database encryption upgraded to Argon2id successfully");
                    _needsLegacyMigration = false;
                }
                catch (AzureBackup.Core.InvalidPasswordException)
                {
                    AddLog("Invalid password for existing database - please try again");
                    return;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Encryption upgrade failed: {ex.Message}");
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return;
                }
            }

            // Step 2: Initialize the encrypted database with password
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, Password);
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                AddLog("Invalid password - please try again");
                return;
            }
            
            // Step 3: Load configuration from the now-unlocked database
            LoadConfiguration();
            
            // For new setups, restore user input that LoadConfiguration may have overwritten
            // (config is empty for new databases, so LoadConfiguration sets defaults)
            if (isNewSetup)
            {
                ConnectionString = userConnectionString;
                ContainerName = userContainerName;
                StorageAccountName = userStorageAccountName;
            }
            
            // Step 4: Initialize encryption service for backup operations
            var success = await _orchestrator.InitializeAsync(Password);
            if (!success)
            {
                AddLog("Failed to initialize encryption");
                return;
            }

            IsInitialized = true;
            AddLog("Encryption initialized successfully!");
            
            // Clear sensitive password data from UI
            Password = string.Empty;
            PasswordConfirm = string.Empty;

            // Step 5: Save and connect to storage (for new users with storage config)
            if (isNewSetup && !_needsMigration)
            {
                // Save watched folders
                var config = _databaseService.GetConfiguration();
                config.WatchedFolders = WatchedFolders.Select(f => f.ToModel()).ToList();
                _databaseService.SaveConfiguration(config);

                // Save storage settings using the preserved user input
                if (UseEntraIdAuth)
                {
                    await _orchestrator.SaveStorageAccountAsync(userStorageAccountName, userContainerName);
                    AddLog("Entra ID settings saved and connected!");
                }
                else
                {
                    await _orchestrator.SaveConnectionStringAsync(userConnectionString, userContainerName);
                    ConnectionString = "[Encrypted - stored securely]";
                    AddLog("Connection string saved (encrypted) and connected!");
                }
            }

            // Step 6: Update status and load files
            IsEntraIdAuthenticated = _orchestrator.IsEntraIdAuthenticated;
            
            // Reload config to check for stored connection
            var finalConfig = _databaseService.GetConfiguration();
            var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(finalConfig.StorageAccountName);
            var hasConnectionStringConfig = !UseEntraIdAuth && finalConfig.EncryptedConnectionString != null;
            
            if (hasEntraIdConfig || hasConnectionStringConfig)
            {
                AddLog("Loading files from Azure...");
                await RefreshFromAzureAsync();
            }
            else if (!isNewSetup)
            {
                // Returning user but no storage configured yet
                if (UseEntraIdAuth)
                {
                    AddLog("Unlocked! Configure storage account and sign in with Microsoft to connect.");
                }
                else
                {
                    AddLog("Unlocked! Enter your connection string and click 'Save & Connect'.");
                }
            }
            
            // Always refresh local files (watched folders) regardless of Azure connection
            await RefreshLocalFilesAsync();
        }
        catch (AzureBackup.Core.SecurityPolicyException ex)
        {
            AddLog($"Security: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            AddLog($"Failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Attempts to unlock the application with the given password.
    /// Used by the startup password dialog.
    /// Handles migration from unencrypted database automatically.
    /// </summary>
    /// <param name="password">The password to try</param>
    /// <returns>Tuple of (success, errorMessage). If success is true, errorMessage is null.</returns>
    public async Task<(bool success, string? errorMessage)> TryUnlockWithPasswordAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return (false, "Please enter a password");
        }

        try
        {
            // Step 1: Handle migration from unencrypted database if needed
            if (_needsMigration)
            {
                AddLog("Migrating database to encrypted format...");
                var tempPath = AppMode.DatabasePath + ".encrypted";
                
                try
                {
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, password);
                    _databaseService.Close();
                    
                    var backupPath = AppMode.DatabasePath + ".unencrypted.bak";
                    System.IO.File.Move(AppMode.DatabasePath, backupPath);
                    System.IO.File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return (false, $"Migration failed: {ex.Message}");
                }
            }
            
            // Step 1b: Handle migration from legacy encrypted database to Argon2id
            if (_needsLegacyMigration)
            {
                AddLog("Upgrading database encryption to Argon2id...");
                var tempPath = AppMode.DatabasePath + ".upgraded";
                
                try
                {
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, password);
                    _databaseService.Close();
                    
                    var backupPath = AppMode.DatabasePath + ".legacy.bak";
                    System.IO.File.Move(AppMode.DatabasePath, backupPath);
                    System.IO.File.Move(tempPath, AppMode.DatabasePath);
                    // Move the salt file too
                    var tempSaltPath = tempPath + ".salt";
                    var finalSaltPath = AppMode.DatabasePath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Move(tempSaltPath, finalSaltPath);
                    File.Delete(backupPath);
                    
                    AddLog("Database encryption upgraded to Argon2id successfully");
                    _needsLegacyMigration = false;
                }
                catch (AzureBackup.Core.InvalidPasswordException)
                {
                    return (false, "Invalid password for existing database");
                }
                catch (System.Exception ex)
                {
                    AddLog($"Encryption upgrade failed: {ex.Message}");
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                    var tempSaltPath = tempPath + ".salt";
                    if (File.Exists(tempSaltPath))
                        File.Delete(tempSaltPath);
                    return (false, $"Encryption upgrade failed: {ex.Message}");
                }
            }

            // Step 2: Initialize the encrypted database with password
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, password);
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                return (false, "Invalid password");
            }
            
            // Step 3: Load configuration from the now-unlocked database
            LoadConfiguration();
            
            // Step 4: Initialize encryption service for backup operations
            var success = await _orchestrator.InitializeAsync(password);
            if (!success)
            {
                return (false, "Failed to initialize encryption");
            }

            IsInitialized = true;
            AddLog("Unlocked successfully!");
            
            // Update Entra ID status
            IsEntraIdAuthenticated = _orchestrator.IsEntraIdAuthenticated;
            
            // Check if Azure storage is configured and load files
            var config = _databaseService.GetConfiguration();
            var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(config.StorageAccountName);
            var hasConnectionStringConfig = !UseEntraIdAuth && config.EncryptedConnectionString != null;
            
            if (hasEntraIdConfig || hasConnectionStringConfig)
            {
                AddLog("Loading files from Azure...");
                await RefreshFromAzureAsync();
                await RefreshLocalFilesAsync();
            }
            
            return (true, null);
        }
        catch (AzureBackup.Core.SecurityPolicyException ex)
        {
            return (false, ex.Message);
        }
        catch (System.Exception ex)
        {
            return (false, $"Unlock failed: {ex.Message}");
        }
    }

    #endregion
}
