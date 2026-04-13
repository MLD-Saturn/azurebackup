using Azure.Core;
using Azure.Identity;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Azure authentication methods: Entra ID and Connection String.
/// </summary>
public partial class BackupOrchestrator
{
    /// <summary>
    /// Authenticates with Microsoft Entra ID using interactive browser flow.
    /// Opens the system's default browser for seamless sign-in.
    /// Use this for organizational/work accounts only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <param name="timeoutSeconds">Timeout in seconds for the authentication (default: 120 seconds)</param>
    public async Task<(bool success, string message)> AuthenticateWithEntraIdAsync(
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 120)
    {
        Log($"AuthenticateWithEntraIdAsync: Starting browser authentication (timeout={timeoutSeconds}s)");
        try
        {
            InteractiveBrowserCredentialOptions options = new()
            {
                // Use the system's default browser
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "AzureBackup",
                    UnsafeAllowUnencryptedStorage = false
                },
                // Redirect to localhost after auth completes
                RedirectUri = new Uri("http://localhost"),
                // Set a reasonable timeout for browser interaction
                BrowserCustomization = new BrowserCustomizationOptions
                {
                    UseEmbeddedWebView = false
                }
            };
            
            _credential = new InteractiveBrowserCredential(options);
            Log("AuthenticateWithEntraIdAsync: Opening browser for authentication");
            
            // Create a timeout cancellation token
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            // Force a token request to trigger the browser login
            TokenRequestContext tokenRequest = new(
                ["https://storage.azure.com/.default"]);
            
            var token = await _credential.GetTokenAsync(tokenRequest, linkedCts.Token);
            
            if (!string.IsNullOrEmpty(token.Token))
            {
                Log("AuthenticateWithEntraIdAsync: Token obtained successfully");
                // Update config to mark as authenticated
                var config = _databaseService.GetConfiguration();
                config.IsEntraIdAuthenticated = true;
                _databaseService.SaveConfiguration(config);
                
                return (true, "Successfully authenticated with Microsoft Entra ID!");
            }
            
            Log("AuthenticateWithEntraIdAsync: No token returned");
            _credential = null;
            return (false, "Authentication did not return a valid token.");
        }
        catch (AuthenticationFailedException ex)
        {
            Log($"AuthenticateWithEntraIdAsync: AuthenticationFailedException - {ex.Message}");
            _credential = null;
            // Provide more user-friendly messages for common errors
            if (ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Sign-in was cancelled. Please try again.");
            }
            if (ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Microsoft sign-in error: {ex.Message}");
            }
            return (false, $"Authentication failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("AuthenticateWithEntraIdAsync: Cancelled by user");
            _credential = null;
            return (false, "Sign-in was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            Log($"AuthenticateWithEntraIdAsync: Timeout after {timeoutSeconds} seconds");
            _credential = null;
            return (false, $"Sign-in timed out after {timeoutSeconds} seconds. Please try again.");
        }
        catch (Exception ex)
        {
            Log($"AuthenticateWithEntraIdAsync: Exception - {ex.GetType().Name}: {ex.Message}");
            _credential = null;
            // Handle browser-related errors
            if (ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Could not open browser for sign-in. Please ensure a browser is available.");
            }
            return (false, $"Authentication error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Tests the connection to Azure storage using the current Entra ID credential.
    /// </summary>
    public async Task<(bool success, string message)> TestAzureConnectionAsync(
        string storageAccountName, string containerName)
    {
        Log($"TestAzureConnectionAsync: Testing Entra ID connection to {storageAccountName}/{containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (_credential == null)
        {
            Log("TestAzureConnectionAsync: No credential available");
            return (false, "Not authenticated with Entra ID. Please sign in first.");
        }
        
        Uri blobServiceUri = new($"https://{storageAccountName}.blob.core.windows.net");
        var result = await _blobService.TestConnectionWithEntraIdAsync(blobServiceUri, containerName, _credential);
        Log($"TestAzureConnectionAsync: Result success={result.success}");
        return result;
    }
    
    /// <summary>
    /// Saves the Azure storage account configuration (uses Entra ID, no connection string needed).
    /// </summary>
    public async Task SaveStorageAccountAsync(string storageAccountName, string containerName)
    {
        Log($"SaveStorageAccountAsync: Saving Entra ID config for {storageAccountName}/{containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (_credential == null)
            throw new InvalidOperationException("Must authenticate with Entra ID first.");
        
        var config = _databaseService.GetConfiguration();
        config.StorageAccountName = storageAccountName;
        config.ContainerName = containerName;
        config.IsEntraIdAuthenticated = true;
        config.AuthMethod = AzureAuthMethod.EntraId;
        _databaseService.SaveConfiguration(config);
        Log("SaveStorageAccountAsync: Configuration saved");
        
        // Connect immediately
        await _blobService.ConnectWithEntraIdAsync(
            config.BlobServiceUri!, 
            containerName, 
            _credential);
        Log("SaveStorageAccountAsync: Connected to Azure storage");
    }
    
    /// <summary>
    /// Gets whether the user is currently authenticated with Entra ID.
    /// </summary>
    public bool IsEntraIdAuthenticated => _credential != null;

    /// <summary>
    /// Tests the connection using a connection string.
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionStringAsync(
        string connectionString, string containerName)
    {
        Log($"TestConnectionStringAsync: Testing connection string to container {containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        var result = await _blobService.TestConnectionAsync(connectionString, containerName);
        Log($"TestConnectionStringAsync: Result success={result.success}");
        return result;
    }

    /// <summary>
    /// Saves and connects using a connection string (encrypts it before storing).
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task SaveConnectionStringAsync(string connectionString, string containerName)
    {
        Log($"SaveConnectionStringAsync: Saving connection string config for container {containerName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        if (!_encryptionService.IsInitialized)
            throw new InvalidOperationException("Encryption service must be initialized before saving connection string.");
        
        var config = _databaseService.GetConfiguration();
        config.EncryptedConnectionString = _encryptionService.Encrypt(
            System.Text.Encoding.UTF8.GetBytes(connectionString));
        config.ContainerName = containerName;
        config.AuthMethod = AzureAuthMethod.ConnectionString;
        config.IsEntraIdAuthenticated = false;
        _databaseService.SaveConfiguration(config);
        Log("SaveConnectionStringAsync: Encrypted connection string saved");
        
        // Connect immediately
        await _blobService.ConnectAsync(connectionString, containerName);
        Log("SaveConnectionStringAsync: Connected to Azure storage");
    }
}
