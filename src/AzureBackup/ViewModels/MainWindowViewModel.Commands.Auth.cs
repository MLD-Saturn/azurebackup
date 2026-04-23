using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core;
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

    /// <summary>
    /// Clears the masked connection string so the user can enter a new one
    /// without resetting the database or losing any data.
    /// </summary>
    [RelayCommand]
    private void UpdateConnectionString()
    {
        ConnectionString = string.Empty;
        IsEditingConnectionString = true;
        AddLog("Enter a new connection string, then click 'Save & Connect'");
    }

    /// <summary>
    /// Cancels editing and restores the masked placeholder.
    /// </summary>
    [RelayCommand]
    private void CancelUpdateConnectionString()
    {
        ConnectionString = "[Encrypted - stored securely]";
        IsEditingConnectionString = false;
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
                if (string.IsNullOrWhiteSpace(ConnectionString) || ConnectionString.StartsWith("[Encrypted", StringComparison.Ordinal))
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
            // Persist WatchedFolders + budget FIRST so the orchestrator's
            // subsequent SaveStorageAccountAsync / SaveConnectionStringAsync
            // (each of which does GetConfiguration -> mutate -> SaveConfiguration)
            // sees and preserves them. Pre-fix the order was reversed: auth
            // saved first, then folders. If the folders write threw, the
            // auth side was already committed and the folder list was lost.
            // Doing watched-folders first means a failure in the second
            // (auth) step at worst leaves the folders updated without the
            // new credential — recoverable, no data loss.
            if (IsInitialized)
            {
                var folderConfig = _databaseService.GetConfiguration();
                folderConfig.WatchedFolders = WatchedFolders.Select(f => f.ToModel()).ToList();
                _databaseService.SaveConfiguration(folderConfig);
            }

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
                if (string.IsNullOrWhiteSpace(ConnectionString) || ConnectionString.StartsWith("[Encrypted", StringComparison.Ordinal))
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
                IsEditingConnectionString = false;
                AddLog("Connection string saved (encrypted) and connected!");
            }
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
        // Snapshot the password into a caller-owned char[] so we can pass it
        // down as a ReadOnlySpan<char> / ReadOnlyMemory<char> and zero it deterministically
        // when the operation finishes. The XAML-bound string is cleared below.
        var passwordChars = new char[Password.Length];
        Password.AsSpan().CopyTo(passwordChars);
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
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, passwordChars);

                    // Close any existing connections and swap files
                    _databaseService.Close();

                    // Crash-safe atomic swap. Sentinel at
                    // {DatabasePath}.upgrade-pending guards every rename so
                    // a process crash mid-rename is recoverable on next
                    // launch via LocalDatabaseService.RecoverInterruptedUpgrade.
                    // Matches the pattern already used by the same migration
                    // step in UnlockAndConnectAsync and TryUnlockWithPasswordAsync;
                    // this site was missed in commit 9902ac1.
                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".unencrypted.bak");

                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    // Failed-mid-write encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
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
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, passwordChars);
                    _databaseService.Close();

                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".legacy.bak");

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
                    // Failed-mid-write Argon2id-encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
                    return;
                }
            }

            // C-5: SQLite is the production default. If the file at
            // DatabasePath is still a LiteDB database (upgrading user)
            // EnsureMigratedToSqliteAsync runs the migration with
            // progress surfaced via AddLog. No-op when the file is
            // already SQLite or does not exist yet.
            if (!await EnsureMigratedToSqliteAsync(passwordChars.AsMemory()))
            {
                return;
            }

            // Step 3: Initialize the encrypted database with password
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, passwordChars.AsSpan());
                AddLog("Database unlocked successfully");
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                AddLog("Invalid password - please try again");
                return;
            }

            // Phase 5 / P3: one-time reverse-chunk-index rebuild if this is an
            // upgraded database. Blocks the login flow (matches the approved sync
            // migration UX) but shows incremental progress via AddLog.
            await EnsureReverseChunkIndexBuiltAsync();

            // Step 4: Load configuration from the now-unlocked database
            LoadConfiguration();

            // Step 5: Initialize encryption service for backup operations
            var success = await _orchestrator.InitializeAsync(passwordChars.AsMemory());
            if (success)
            {
                IsInitialized = true;
                AddLog("Encryption initialized successfully!");

                // Check if Azure connection failed during initialization
                if (_orchestrator.AzureConnectionError != null)
                {
                    AddLog($"Warning: Azure connection failed - {_orchestrator.AzureConnectionError}");
                    AddLog("You can update connection settings in the Settings tab.");
                }

                // Clear sensitive data from UI memory
                Password = string.Empty;
                PasswordConfirm = string.Empty;

                // Update Entra ID status
                IsEntraIdAuthenticated = _orchestrator.IsEntraIdAuthenticated;

                // Check if Azure storage is configured and connected
                var config = _databaseService.GetConfiguration();
                var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(config.StorageAccountName);
                var hasConnectionStringConfig = !UseEntraIdAuth && config.EncryptedConnectionString != null;

                if (_blobService.IsConnected && (hasEntraIdConfig || hasConnectionStringConfig))
                {
                    AddLog("Loading files from Azure...");
                    await RefreshFromAzureAsync();
                }
                else if (_orchestrator.AzureConnectionError == null)
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
            // Zero the caller-owned char[] and drop the bound string reference.
            System.Array.Clear(passwordChars);
            Password = string.Empty;
            PasswordConfirm = string.Empty;
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
                    if (string.IsNullOrWhiteSpace(userConnectionString) || userConnectionString.StartsWith("[Encrypted", StringComparison.Ordinal))
                    {
                        AddLog("Please enter a connection string");
                        return;
                    }
                }
            }
        }

        IsOperationInProgress = true;
        // Snapshot the password into a caller-owned char[] so we can pass it
        // down as a ReadOnlySpan<char> / ReadOnlyMemory<char> and zero it deterministically.
        var passwordChars = new char[Password.Length];
        Password.AsSpan().CopyTo(passwordChars);
        try
        {
            // Step 1: Handle migration from unencrypted database if needed
            if (_needsMigration)
            {
                AddLog("Migrating database to encrypted format...");
                var tempPath = AppMode.DatabasePath + ".encrypted";

                try
                {
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, passwordChars);
                    _databaseService.Close();

                    // Crash-safe atomic swap. Sentinel at
                    // {DatabasePath}.upgrade-pending guards every rename so
                    // a process crash mid-rename is recoverable on next
                    // launch via LocalDatabaseService.RecoverInterruptedUpgrade.
                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".unencrypted.bak");

                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    // Failed-mid-write encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
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
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, passwordChars);
                    _databaseService.Close();

                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".legacy.bak");

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
                    // Failed-mid-write Argon2id-encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
                    return;
                }
            }

            // C-5: detect a LiteDB database left over from a prior
            // release and migrate to SQLite (no-op on fresh installs).
            if (!await EnsureMigratedToSqliteAsync(passwordChars.AsMemory()))
            {
                return;
            }

            // Step 2: Initialize the encrypted database with password
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, passwordChars.AsSpan());
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                AddLog("Invalid password - please try again");
                return;
            }

            await EnsureReverseChunkIndexBuiltAsync();

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
            var success = await _orchestrator.InitializeAsync(passwordChars.AsMemory());
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

            // Check if Azure connection failed during initialization
            if (_orchestrator.AzureConnectionError != null)
            {
                AddLog($"Warning: Azure connection failed - {_orchestrator.AzureConnectionError}");
                AddLog("You can update connection settings in the Settings tab.");
            }

            // Reload config to check for stored connection
            var finalConfig = _databaseService.GetConfiguration();
            var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(finalConfig.StorageAccountName);
            var hasConnectionStringConfig = !UseEntraIdAuth && finalConfig.EncryptedConnectionString != null;

            if (_blobService.IsConnected && (hasEntraIdConfig || hasConnectionStringConfig))
            {
                AddLog("Loading files from Azure...");
                await RefreshFromAzureAsync();
            }
            else if (_orchestrator.AzureConnectionError == null && !isNewSetup)
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
            System.Array.Clear(passwordChars);
            Password = string.Empty;
            PasswordConfirm = string.Empty;
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Attempts to unlock the application with the given password.
    /// Used by the startup password dialog.
    /// Handles migration from unencrypted database automatically.
    /// </summary>
    /// <param name="password">Password characters owned by the caller. The caller is
    /// responsible for clearing this array with <see cref="System.Array.Clear(System.Array)"/>
    /// after this method returns.</param>
    /// <returns>Tuple of (success, errorMessage). If success is true, errorMessage is null.</returns>
    public async Task<(bool success, string? errorMessage)> TryUnlockWithPasswordAsync(char[] password)
    {
        if (password is null || password.Length == 0)
        {
            return (false, "Please enter a password");
        }

        // Check for whitespace-only input without allocating a string.
        bool hasNonWhitespace = false;
        foreach (var c in password)
        {
            if (!char.IsWhiteSpace(c)) { hasNonWhitespace = true; break; }
        }
        if (!hasNonWhitespace)
        {
            return (false, "Please enter a password");
        }

        var passwordMemory = password.AsMemory();

        try
        {
            // Step 1: Handle migration from unencrypted database if needed
            if (_needsMigration)
            {
                AddLog("Migrating database to encrypted format...");
                var tempPath = AppMode.DatabasePath + ".encrypted";

                try
                {
                    LocalDatabaseService.MigrateToEncrypted(AppMode.DatabasePath, tempPath, passwordMemory.Span);
                    _databaseService.Close();

                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".unencrypted.bak");

                    AddLog("Database migration completed successfully");
                    _needsMigration = false;
                }
                catch (System.Exception ex)
                {
                    AddLog($"Migration failed: {ex.Message}");
                    // Failed-mid-write encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
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
                    LocalDatabaseService.MigrateLegacyEncrypted(AppMode.DatabasePath, tempPath, passwordMemory.Span);
                    _databaseService.Close();

                    LocalDatabaseService.CommitDatabaseUpgrade(
                        AppMode.DatabasePath, tempPath, ".legacy.bak");

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
                    // Failed-mid-write Argon2id-encrypted DB + its salt -- secret-bearing.
                    FileSystemHelper.TrySecureDelete(tempPath);
                    FileSystemHelper.TrySecureDelete(tempPath + ".salt");
                    return (false, $"Encryption upgrade failed: {ex.Message}");
                }
            }

            // C-5: detect a LiteDB database left over from a prior
            // release and migrate to SQLite (no-op on fresh installs).
            if (!await EnsureMigratedToSqliteAsync(passwordMemory))
            {
                return (false, "Migration to SQLite failed - see log for details");
            }

            // Step 2: Initialize the encrypted database with password
            AddLog("Unlock step 2: opening encrypted database...");
            try
            {
                _databaseService.Initialize(AppMode.DatabasePath, passwordMemory.Span);
            }
            catch (AzureBackup.Core.InvalidPasswordException)
            {
                return (false, "Invalid password");
            }
            AddLog("Unlock step 2: database open OK");

            AddLog("Unlock step 3: ensuring reverse chunk index...");
            await EnsureReverseChunkIndexBuiltAsync();
            AddLog("Unlock step 3: reverse chunk index OK");

            // Step 3: Load configuration from the now-unlocked database
            AddLog("Unlock step 4: loading configuration...");
            LoadConfiguration();
            AddLog("Unlock step 4: configuration loaded OK");

            // Step 4: Initialize encryption service for backup operations
            AddLog("Unlock step 5: initializing encryption service (Argon2id verify + derive)...");
            var success = await _orchestrator.InitializeAsync(passwordMemory);
            AddLog($"Unlock step 5: orchestrator.InitializeAsync returned {success}");
            if (!success)
            {
                return (false, "Failed to initialize encryption");
            }

            IsInitialized = true;
            AddLog("Unlocked successfully!");

            // Check if Azure connection failed during initialization
            if (_orchestrator.AzureConnectionError != null)
            {
                AddLog($"Warning: Azure connection failed - {_orchestrator.AzureConnectionError}");
                AddLog("You can update connection settings in the Settings tab.");
            }

            // Update Entra ID status
            IsEntraIdAuthenticated = _orchestrator.IsEntraIdAuthenticated;
            
            // Check if Azure storage is configured and load files
            var config = _databaseService.GetConfiguration();
            var hasEntraIdConfig = IsEntraIdAuthenticated && !string.IsNullOrEmpty(config.StorageAccountName);
            var hasConnectionStringConfig = !UseEntraIdAuth && config.EncryptedConnectionString != null;

            if (_blobService.IsConnected && (hasEntraIdConfig || hasConnectionStringConfig))
            {
                AddLog("Loading files from Azure...");
                await RefreshFromAzureAsync();
            }

            await RefreshLocalFilesAsync();

            // Return success with optional Azure warning
            var azureWarning = _orchestrator.AzureConnectionError;
            return azureWarning != null
                ? (true, $"Unlocked, but Azure is unavailable: {azureWarning}")
                : (true, null);
        }
        catch (AzureBackup.Core.SecurityPolicyException ex)
        {
            return (false, ex.Message);
        }
        catch (AzureBackup.Core.InsufficientMemoryForKdfException ex)
        {
            // B11: NOT a wrong-password situation; do not consume an
            // attempt counter on the user's behalf. Surface the message
            // verbatim so the user sees the actionable hint ("close
            // other apps / restart").
            AddLog($"Unlock failed (memory pressure): {ex.Message}");
            if (ex.InnerException != null)
            {
                AddLog($"  Underlying: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            return (false, ex.Message);
        }
        catch (AzureBackup.Core.InvalidPasswordException)
        {
            return (false, "Invalid password");
        }
        catch (System.Exception ex) when (
            ex is OutOfMemoryException)
        {
            // Defensive: an OOM that escapes the SqliteBackend's typed
            // wrapping (e.g. from EncryptionService.Initialize, which
            // also runs Argon2id) reaches here as the raw runtime
            // exception. Render with the same "actionable" message
            // shape.
            // B15: emit FULL exception detail so a tester can pinpoint
            // which call site OOM'd. Pre-B15 we logged only ex.Message
            // ("Insufficient memory to continue the execution of the
            // program.") which gave no clue WHERE in the call chain it
            // happened. The stack trace will identify the failing
            // method.
            AddLog($"Unlock failed (memory pressure): {ex.GetType().FullName}: {ex.Message}");
            AddLog($"  Stack trace:");
            foreach (var line in (ex.StackTrace ?? "(no stack trace)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                AddLog($"    {line.TrimEnd()}");
            }
            if (ex.InnerException != null)
            {
                AddLog($"  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }
            return (false, "Insufficient memory for key derivation. Close other applications and try again.");
        }
        catch (System.Exception ex) when (
            ex is OverflowException ||
            ex is ArgumentOutOfRangeException ||
            ex is IndexOutOfRangeException ||
            ex is System.Security.Cryptography.CryptographicException)
        {
            // Defensive: a wrong password that decrypts the SQLite header
            // into garbage occasionally slips past SqliteBackend's own
            // detection (the schema probe sees a row that "looks valid")
            // and trips a downstream allocation/decrypt path. Without
            // this branch the user sees an opaque .NET runtime message
            // (e.g. "Array dimensions exceeded supported range") with
            // no hint that the actual cause is a typo. Real bug observed
            // by tester. AddLog the underlying message so a corrupted-
            // database case is still diagnosable from the log pane.
            AddLog($"Unlock failed (treated as invalid password): {ex.GetType().Name}: {ex.Message}");
            return (false, "Invalid password (or database may be corrupted -- see Logs tab for details)");
        }
        catch (System.Exception ex)
        {
            return (false, $"Unlock failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="TryUnlockWithPasswordAsync(char[])"/>.
    /// Prefer the <c>char[]</c> overload so the plaintext password can be zeroed after use.
    /// This wrapper copies into a temporary <c>char[]</c> and clears it before returning.
    /// </summary>
    public async Task<(bool success, string? errorMessage)> TryUnlockWithPasswordAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return (false, "Please enter a password");
        }

        var buffer = new char[password.Length];
        password.AsSpan().CopyTo(buffer);
        try
        {
            return await TryUnlockWithPasswordAsync(buffer);
        }
        finally
        {
            System.Array.Clear(buffer);
        }
    }

    #endregion

    #region SQLite migration (Option C / C-2)

    /// <summary>
    /// Probes the database file at <see cref="AppMode.DatabasePath"/>
    /// and runs the LiteDB-to-SQLite migration if needed. Called from
    /// each of the three login flows BEFORE
    /// <c>_databaseService.Initialize</c>.
    /// </summary>
    /// <param name="passwordMemory">Caller-owned password buffer.
    /// Used to open both the LiteDB source and the SQLite destination.
    /// Accepts <see cref="ReadOnlyMemory{T}"/> rather than
    /// <see cref="ReadOnlySpan{T}"/> because the engine call must run
    /// on a worker thread (where spans cannot be captured); the two
    /// existing in-process callers already hold the password as
    /// either <c>char[]</c> or <see cref="ReadOnlyMemory{T}"/> and
    /// trivially adapt.</param>
    /// <returns>
    /// True if Initialize should proceed normally. False if migration
    /// failed (caller should bail out without calling Initialize).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Decision tree (C-5: SQLite is unconditional in production;
    /// LiteDB only reachable via an AsyncLocal test override):
    /// </para>
    /// <list type="bullet">
    ///   <item>Test override pinning LiteDB -- skip; the test is
    ///     intentionally working with a LiteDB database.</item>
    ///   <item>No file at path -- new database; return true.</item>
    ///   <item>File IS already SQLite (probe true) -- run
    ///     <see cref="LocalDatabaseService.CleanupStaleLegacyBackup"/>
    ///     to remove any leftover .litedb-backup from a pre-C-5
    ///     release, then return true.</item>
    ///   <item>File is LiteDB (probe false) -- run migration with
    ///     throttled progress, return true on success. Migration's
    ///     step 4 deletes the .litedb-backup files.</item>
    /// </list>
    ///
    /// <para>
    /// Mirrors the shape of <see cref="EnsureReverseChunkIndexBuiltAsync"/>:
    /// <c>Task.Run</c> wraps the synchronous engine call so the UI
    /// thread stays responsive, and progress reports are throttled to
    /// roughly every 5% so the log panel does not flood. Migration is
    /// non-cancellable from the UI by design (eval doc says forced
    /// migration; user complaints about a 10-15 s freeze are
    /// preferable to a half-migrated state on cancel).
    /// </para>
    /// </remarks>
    private async Task<bool> EnsureMigratedToSqliteAsync(ReadOnlyMemory<char> passwordMemory)
    {
        // C-5: SQLite is the production default. There is no longer a
        // backend-choice gate to honour - if there is data on disk and
        // it is not yet SQLite, we migrate.
        if (!File.Exists(AppMode.DatabasePath))
        {
            // New database: SqliteBackend will create one fresh on
            // Initialize. Defensive: also tidy any stale .litedb-backup
            // (edge case - user deleted the live DB but left the
            // backup behind from a previous install).
            LocalDatabaseService.CleanupStaleLegacyBackup(AppMode.DatabasePath);
            return true;
        }

        if (LocalDatabaseService.IsExistingSqliteDatabase(
                AppMode.DatabasePath, passwordMemory.Span))
        {
            // Already SQLite. Initialize will open it directly.
            // C-5 cleanup: a user who migrated under the prior
            // retention policy still has a .litedb-backup on disk.
            // Delete it now so the data directory ends up containing
            // only SQLite files.
            LocalDatabaseService.CleanupStaleLegacyBackup(AppMode.DatabasePath);
            return true;
        }

        // The file at the target path opens cleanly as LiteDB but not
        // SQLite. Run migration.
        AddLog("Migrating local database to SQLite (one-time upgrade)...");
        AddLog("  This typically takes 10-30 seconds. Please do not close the app.");

        // Throttle progress reports so we do not spam AddLog at every row.
        var lastReportedPct = -5;
        var progress = new Progress<(int processed, int total)>(tuple =>
        {
            var (processed, total) = tuple;
            if (total <= 0) return;
            var pct = (int)((long)processed * 100 / total);
            if (pct - lastReportedPct >= 5 || processed == total)
            {
                lastReportedPct = pct;
                AddLog($"  Migration progress: {processed:N0} / {total:N0} rows ({pct}%)");
            }
        });

        try
        {
            // Engine call is synchronous; offload to a worker thread so
            // the dispatcher can pump the AddLog progress reports.
            // Capture passwordMemory by value into the lambda; the
            // caller owns the underlying buffer and zeros it.
            await Task.Run(() => LocalDatabaseService.MigrateFromLiteDb(
                AppMode.DatabasePath, passwordMemory.Span, progress));
            AddLog("Migration to SQLite complete. Original LiteDB preserved as .litedb-backup.");
            return true;
        }
        catch (AzureBackup.Core.InvalidPasswordException)
        {
            // Same surface as the LiteDB-side InvalidPasswordException
            // each call site already handles below.
            AddLog("Invalid password - please try again");
            return false;
        }
        catch (Exception ex)
        {
            AddLog($"Migration to SQLite failed: {ex.Message}");
            AddLog("  Your data is unchanged; the LiteDB database is still authoritative.");
            return false;
        }
    }

    #endregion

    #region Reverse Chunk Index Migration (Phase 5 / P3)

    /// <summary>
    /// Runs the one-time <c>chunk_file_refs</c> rebuild on an upgraded database.
    /// No-op when already built (common case). Executed on a background thread so
    /// the UI dispatcher stays responsive; progress is surfaced via
    /// <c>AddLog</c> at ~every 5% of total so large indexes do not flood the log.
    /// </summary>
    /// <remarks>
    /// This method blocks the caller until the rebuild finishes, which matches
    /// the approved synchronous migration UX. For a freshly-initialised database
    /// the rebuild is trivially cheap (0 rows) and returns in microseconds.
    /// </remarks>
    private async Task EnsureReverseChunkIndexBuiltAsync()
    {
        // Start the periodic WAL checkpoint timer once per app session. Doing this
        // here (rather than in the ctor) guarantees the database is initialised
        // before the first tick fires.
        StartCheckpointTimerIfNotRunning();

        if (_databaseService.IsReverseChunkIndexBuilt())
        {
            return;
        }

        AddLog("Building reverse chunk index (one-time upgrade)...");

        // Throttle progress reports so we don't spam AddLog at every chunk.
        var lastReportedPct = -5;
        var progress = new Progress<(int processed, int total)>(tuple =>
        {
            var (processed, total) = tuple;
            if (total <= 0) return;
            var pct = (int)((long)processed * 100 / total);
            if (pct - lastReportedPct >= 5 || processed == total)
            {
                lastReportedPct = pct;
                AddLog($"  Reverse index progress: {processed:N0} / {total:N0} chunks ({pct}%)");
            }
        });

        try
        {
            await Task.Run(() => _databaseService.RebuildReverseChunkIndex(progress));
            AddLog("Reverse chunk index built.");
        }
        catch (Exception ex)
        {
            AddLog($"Reverse chunk index rebuild failed: {ex.Message}");
            // Let the caller's main flow continue; GetChunkEntriesForFile will
            // simply return empty until the next successful rebuild attempt.
        }
    }

    /// <summary>
    /// Starts the hourly LiteDB WAL checkpoint timer if it has not been started
    /// yet. Idempotent; the three login paths may all call this safely.
    /// </summary>
    private void StartCheckpointTimerIfNotRunning()
    {
        if (_checkpointTimer is not null) return;

        // One hour cadence: aggressive enough to prevent multi-GB WAL bloat during
        // continuous-backup sessions but infrequent enough that the brief
        // write-lock window during checkpoint stays well below perceptible.
        var interval = TimeSpan.FromHours(1);
        _checkpointTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    _databaseService.Checkpoint();
                }
                catch (Exception ex)
                {
                    // A background-timer exception must never crash the process.
                    AddLog($"Periodic WAL checkpoint failed: {ex.Message}");
                }
            },
            state: null,
            dueTime: interval,
            period: interval);
    }

    #endregion
}
