using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Handles all interactions with Azure Blob Storage.
/// Supports both Entra ID and Connection String authentication.
/// Uploads encrypted chunks to Cool tier for cost optimization.
/// Uses parallel transfers to maximize bandwidth utilization.
/// </summary>
public partial class AzureBlobService : IBlobStorageService
{
    private BlobServiceClient? _serviceClient;
    private BlobContainerClient? _containerClient;
    private readonly EncryptionService _encryptionService;
    
    // Cost tracking (approximate Cool tier pricing)
    private const decimal CostPerGbStorage = 0.0115m;  // Cool tier $/GB/month
    private const decimal CostPerWriteOp = 0.00001m;   // $/operation
    private const decimal CostPerReadOp = 0.000001m;   // $/operation
    
    // Transfer optimization settings
    // These settings maximize bandwidth by using parallel block transfers within each blob
    private static readonly StorageTransferOptions DefaultTransferOptions = new()
    {
        MaximumConcurrency = 8,              // Parallel block uploads/downloads per blob
        MaximumTransferSize = 8 * 1024 * 1024,  // 8 MB blocks for large files
        InitialTransferSize = 8 * 1024 * 1024   // Start with 8 MB initial transfer
    };
    
    // Regex for validating blob names (SHA-256 hex string)
    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex ValidHashPattern();
    
    // Regex for validating metadata blob names (Base64-like with replacements)
    [GeneratedRegex(@"^[A-Za-z0-9_\-=]+$", RegexOptions.Compiled)]
    private static partial Regex ValidMetadataHashPattern();
    
    public bool IsConnected => _containerClient != null;
    public long TotalBytesUploaded { get; private set; }
    public int TotalOperations { get; private set; }
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [BlobService] {message}");
    }

    public AzureBlobService(EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(encryptionService);
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Validates a chunk hash to prevent path traversal attacks.
    /// </summary>
    private static void ValidateChunkHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new SecurityPolicyException("Chunk hash cannot be empty", SecurityPolicyType.InvalidBlobName);
        
        
        if (!ValidHashPattern().IsMatch(hash))
            throw new SecurityPolicyException($"Invalid chunk hash format: {hash}", SecurityPolicyType.InvalidBlobName);
    }
    
    /// <summary>
    /// Validates a metadata hash to prevent path traversal attacks.
    /// </summary>
    private static void ValidateMetadataHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new SecurityPolicyException("Metadata hash cannot be empty", SecurityPolicyType.InvalidBlobName);
        
        if (!ValidMetadataHashPattern().IsMatch(hash) || hash.Length > 100)
            throw new SecurityPolicyException($"Invalid metadata hash format", SecurityPolicyType.InvalidBlobName);
    }

    #region Connection String Authentication

    /// <summary>
    /// Initializes connection to Azure Blob Storage using a connection string.
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task ConnectAsync(string connectionString, string containerName)
    {
        Log($"ConnectAsync: Connecting with connection string to container '{containerName}'");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        _serviceClient = new BlobServiceClient(connectionString);
        _containerClient = _serviceClient.GetBlobContainerClient(containerName);
        
        // Create container if it doesn't exist
        await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
        Log("ConnectAsync: Connection established successfully");
    }

    /// <summary>
    /// Tests the connection to Azure Blob Storage using a connection string.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(string connectionString, string containerName)
    {
        Log($"TestConnectionAsync: Testing connection string to container '{containerName}'");
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
            
            BlobServiceClient testClient = new(connectionString);
            var testContainer = testClient.GetBlobContainerClient(containerName);
            
            // Try to get container properties or create it
            await testContainer.CreateIfNotExistsAsync(PublicAccessType.None);
            await testContainer.GetPropertiesAsync();
            
            Log("TestConnectionAsync: Test successful");
            return (true, "Connection successful!");
        }
        catch (RequestFailedException ex)
        {
            Log($"TestConnectionAsync: RequestFailedException - {ex.Status} {ex.Message}");
            Log($"TestConnectionAsync: RequestFailedException - {ex.Status} {ex.Message}");
            return (false, $"Azure error: {ex.Message}");
        }
        catch (FormatException ex)
        {
            Log($"TestConnectionAsync: FormatException - {ex.Message}");
            return (false, "Invalid connection string format. Please check the connection string.");
        }
        catch (Exception ex)
        {
            Log($"TestConnectionAsync: Exception - {ex.GetType().Name}: {ex.Message}");
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    #endregion

    #region Entra ID Authentication

    /// <summary>
    /// Initializes connection to Azure Blob Storage using Entra ID (TokenCredential).
    /// Use this for organizational/work accounts.
    /// </summary>
    public async Task ConnectWithEntraIdAsync(Uri blobServiceUri, string containerName, TokenCredential credential)
    {
        Log($"ConnectWithEntraIdAsync: Connecting with Entra ID to {blobServiceUri}, container '{containerName}'");
        ArgumentNullException.ThrowIfNull(blobServiceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(credential);
        
        _serviceClient = new BlobServiceClient(blobServiceUri, credential);
        _containerClient = _serviceClient.GetBlobContainerClient(containerName);
        
        // Create container if it doesn't exist
        await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
        Log("ConnectWithEntraIdAsync: Connection established successfully");
    }

    /// <summary>
    /// Tests the connection to Azure Blob Storage using Entra ID.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionWithEntraIdAsync(
        Uri blobServiceUri, string containerName, TokenCredential credential)
    {
        Log($"TestConnectionWithEntraIdAsync: Testing Entra ID to {blobServiceUri}, container '{containerName}'");
        try
        {
            ArgumentNullException.ThrowIfNull(blobServiceUri);
            ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
            ArgumentNullException.ThrowIfNull(credential);
            
            BlobServiceClient testClient = new(blobServiceUri, credential);
            var testContainer = testClient.GetBlobContainerClient(containerName);
            
            // Try to get container properties or create it
            await testContainer.CreateIfNotExistsAsync(PublicAccessType.None);
            await testContainer.GetPropertiesAsync();
            
            Log("TestConnectionWithEntraIdAsync: Test successful");
            return (true, "Connection successful! Authenticated with Microsoft Entra ID.");
        }
        catch (AuthenticationFailedException ex)
        {
            Log($"TestConnectionWithEntraIdAsync: AuthenticationFailedException - {ex.Message}");
            if (ex.Message.Contains("AADSTS500200", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Personal Microsoft accounts are not supported with Entra ID. Please use the Connection String option instead.");
            }
            return (false, $"Authentication failed: {ex.Message}. Ensure you have 'Storage Blob Data Contributor' role on the storage account.");
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return (false, $"Access denied: {ex.Message}. Ensure you have 'Storage Blob Data Contributor' role on the storage account.");
        }
        catch (RequestFailedException ex)
        {
            return (false, $"Azure error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    #endregion




    #region Blob Operations

    /// <summary>
    /// Uploads an encrypted chunk to blob storage.
    /// Uses Cool tier for cost optimization and parallel block transfers for large chunks.
    /// </summary>
    public async Task<string> UploadChunkAsync(byte[] chunkData, string chunkHash, 
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(chunkData);
        ValidateChunkHash(chunkHash);
        Log($"UploadChunkAsync: Uploading chunk {chunkHash[..8]}... ({chunkData.Length} bytes)");

        // Encrypt the chunk before upload
        var encryptedData = _encryptionService.Encrypt(chunkData);
        
        // Use hash as blob name (content-addressable storage)
        var blobName = $"chunks/{chunkHash}";
        var blobClient = _containerClient!.GetBlobClient(blobName);

        // Check if chunk already exists (deduplication)
        if (await blobClient.ExistsAsync(cancellationToken))
        {
            Log($"UploadChunkAsync: Chunk already exists (dedup), skipping upload");
            progress?.Report(encryptedData.Length);
            return blobName;
        }

        // Upload with Cool tier and parallel transfer options for large chunks
        BlobUploadOptions options = new()
        {
            AccessTier = AccessTier.Cool,
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/octet-stream"
            },
            TransferOptions = DefaultTransferOptions
        };

        await using MemoryStream stream = new(encryptedData);
        await blobClient.UploadAsync(stream, options, cancellationToken);
        
        TotalBytesUploaded += encryptedData.Length;
        TotalOperations++;
        progress?.Report(encryptedData.Length);
        Log($"UploadChunkAsync: Chunk uploaded successfully ({encryptedData.Length} bytes encrypted)");

        return blobName;
    }

    /// <summary>
    /// Uploads an encrypted chunk directly without checking if it exists.
    /// Use this for new files where all chunks are guaranteed to be new.
    /// This reduces API calls by 50% for new file uploads.
    /// </summary>
    public async Task<string> UploadChunkDirectAsync(byte[] chunkData, string chunkHash, 
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(chunkData);
        ValidateChunkHash(chunkHash);
        Log($"UploadChunkDirectAsync: Direct upload chunk {chunkHash[..8]}... ({chunkData.Length} bytes)");

        // Encrypt the chunk before upload
        var encryptedData = _encryptionService.Encrypt(chunkData);
        
        // Use hash as blob name (content-addressable storage)
        var blobName = $"chunks/{chunkHash}";
        var blobClient = _containerClient!.GetBlobClient(blobName);

        // Upload directly without existence check - for new files this saves an API call per chunk
        BlobUploadOptions options = new()
        {
            AccessTier = AccessTier.Cool,
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/octet-stream"
            },
            TransferOptions = DefaultTransferOptions
        };

        await using MemoryStream stream = new(encryptedData);
        await blobClient.UploadAsync(stream, options, cancellationToken);
        
        TotalBytesUploaded += encryptedData.Length;
        TotalOperations++;
        progress?.Report(encryptedData.Length);
        Log($"UploadChunkDirectAsync: Chunk uploaded successfully ({encryptedData.Length} bytes encrypted)");

        return blobName;
    }

    /// <summary>
    /// Uploads file metadata (encrypted).
    /// </summary>
    public async Task UploadFileMetadataAsync(BackedUpFile fileInfo, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(fileInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileInfo.LocalPath);
        Log($"UploadFileMetadataAsync: Uploading metadata for '{Path.GetFileName(fileInfo.LocalPath)}'");

        // Serialize metadata with version for future compatibility
        var metadata = JsonSerializer.Serialize(new MetadataDto(
            fileInfo.LocalPath,
            fileInfo.FileSize,
            fileInfo.LastModified,
            fileInfo.FileHash,
            fileInfo.Chunks.Select(c => new ChunkDto(c.Index, c.Hash, c.Offset, c.Length)).ToList(),
            fileInfo.MetadataVersion
        ));
        
        var encryptedMetadata = _encryptionService.Encrypt(System.Text.Encoding.UTF8.GetBytes(metadata));
        
        // Store under path hash (deterministic for updates)
        var metadataHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fileInfo.LocalPath)));
        var blobName = $"metadata/{metadataHash}";
        
        var blobClient = _containerClient!.GetBlobClient(blobName);
        
        BlobUploadOptions options = new()
        {
            AccessTier = AccessTier.Cool
        };

        await using MemoryStream stream = new(encryptedMetadata);
        await blobClient.UploadAsync(stream, options, cancellationToken);
        
        TotalOperations++;
    }

    /// <summary>
    /// Downloads and decrypts a chunk using parallel transfers for large chunks.
    /// </summary>
    public async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        
        // Validate blob name format
        if (!blobName.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);
        
        try
        {
            await using MemoryStream stream = new();
            
            // Use parallel transfer options for faster downloads
            BlobDownloadToOptions downloadOptions = new()
            {
                TransferOptions = DefaultTransferOptions
            };
            
            await blobClient.DownloadToAsync(stream, downloadOptions, cancellationToken);
            
            var encryptedData = stream.ToArray();
            TotalOperations++;
            
            return _encryptionService.Decrypt(encryptedData);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new DataIntegrityException($"Chunk not found: {blobName}", blobName, ex);
        }
    }

    /// <summary>
    /// Lists all backed up files by retrieving metadata blobs.
    /// </summary>
    public async Task<List<string>> ListMetadataBlobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        List<string> blobs = new();
        
        await foreach (var blob in _containerClient!.GetBlobsAsync(prefix: "metadata/", cancellationToken: cancellationToken))
        {
            blobs.Add(blob.Name);
        }
        
        TotalOperations++;
        return blobs;
    }

    /// <summary>
    /// Downloads and decrypts file metadata.
    /// </summary>
    public async Task<BackedUpFile?> DownloadFileMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        try
        {
            var blobClient = _containerClient!.GetBlobClient(blobName);
            
            await using MemoryStream stream = new();
            await blobClient.DownloadToAsync(stream, cancellationToken);
            
            var encryptedData = stream.ToArray();
            TotalOperations++;
            
            var decryptedData = _encryptionService.Decrypt(encryptedData);
            var json = System.Text.Encoding.UTF8.GetString(decryptedData);
            
            var metadata = JsonSerializer.Deserialize<MetadataDto>(json);
            if (metadata == null) 
                throw new DataIntegrityException($"Failed to deserialize metadata for {blobName}", blobName);

            // Validate chunk sequence
            var sortedChunks = metadata.Chunks.OrderBy(c => c.Index).ToList();
            for (int i = 0; i < sortedChunks.Count; i++)
            {
                if (sortedChunks[i].Index != i)
                    throw new DataIntegrityException(
                        $"Invalid chunk sequence in {blobName}: expected index {i}, found {sortedChunks[i].Index}", 
                        blobName);
            }

            return new BackedUpFile
            {
                LocalPath = metadata.LocalPath,
                FileSize = metadata.FileSize,
                LastModified = metadata.LastModified,
                FileHash = metadata.FileHash,
                MetadataVersion = metadata.Version,
                Chunks = sortedChunks.Select(c => new ChunkInfo
                {
                    Index = c.Index,
                    Hash = c.Hash,
                    Offset = c.Offset,
                    Length = c.Length,
                    BlobName = $"chunks/{c.Hash}"
                }).ToList(),
                Status = BackupStatus.Completed
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // Blob doesn't exist - acceptable
        }
        catch (DataIntegrityException)
        {
            throw; // Re-throw our custom exceptions
        }
        catch (JsonException ex)
        {
            throw new DataIntegrityException($"Metadata corrupted for {blobName}", blobName, ex);
        }
    }

    /// <summary>
    /// Deletes a blob (used for cleanup).
    /// </summary>
    public async Task DeleteBlobAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        
        var blobClient = _containerClient!.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        TotalOperations++;
    }

    /// <summary>
    /// Estimates monthly storage cost based on current usage.
    /// </summary>
    public async Task<(long totalBytes, decimal estimatedMonthlyCost)> GetStorageStatsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        long totalBytes = 0;
        
        await foreach (var blob in _containerClient!.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            totalBytes += blob.Properties.ContentLength ?? 0;
        }
        
        var gbStored = totalBytes / (1024m * 1024m * 1024m);
        var estimatedCost = gbStored * CostPerGbStorage;
        
        return (totalBytes, estimatedCost);
    }

    /// <summary>
    /// Gets the estimated cost for operations performed.
    /// </summary>
    public decimal GetEstimatedOperationsCost()
    {
        return TotalOperations * CostPerWriteOp;
    }

    #endregion

    private void EnsureConnected()
    {
        if (_containerClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage. Call ConnectAsync or ConnectWithEntraIdAsync first.");
    }

    public async ValueTask DisposeAsync()
    {
        // BlobServiceClient doesn't require disposal, but we clear references
        _serviceClient = null;
        _containerClient = null;
        await Task.CompletedTask;
    }

    private record MetadataDto(
        string LocalPath,
        long FileSize,
        DateTime LastModified,
        string FileHash,
        List<ChunkDto> Chunks,
        int Version = 1);

    private record ChunkDto(int Index, string Hash, long Offset, int Length);
}
