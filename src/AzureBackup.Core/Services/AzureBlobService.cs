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
    /// Converts the application's StorageTier enum to Azure's AccessTier.
    /// </summary>
    private static AccessTier ToAccessTier(StorageTier tier) => tier switch
    {
        StorageTier.Hot => AccessTier.Hot,
        StorageTier.Cool => AccessTier.Cool,
        StorageTier.Cold => AccessTier.Cold,
        _ => AccessTier.Cool // Default to Cool for unknown values
    };

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
    /// Uses parallel block transfers for large chunks.
    /// </summary>
    public async Task<string> UploadChunkAsync(byte[] chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(chunkData);
        ValidateChunkHash(chunkHash);
        Log($"UploadChunkAsync: Uploading chunk {chunkHash[..8]}... ({chunkData.Length} bytes) to {storageTier} tier");

        // Encrypt the chunk before upload
        var encryptedData = _encryptionService.Encrypt(chunkData);
        
        // Use hash as blob name (content-addressable storage)
        var blobName = $"chunks/{chunkHash}";
        var blobClient = _containerClient!.GetBlobClient(blobName);

        // Check if chunk already exists (deduplication)
        if (await blobClient.ExistsAsync(cancellationToken))
        {
            // Defense-in-depth: Verify the stored chunk actually matches our data
            // This guards against the astronomically rare case of a SHA-256 collision
            // or more likely scenarios like data corruption or tampering
            bool isCollision = false;
            try
            {
                await VerifyChunkIntegrityAsync(chunkHash, chunkData, cancellationToken);
            }
            catch (HashCollisionException ex)
            {
                // CRITICAL: Hash collision detected - we must upload this chunk with a different name
                // to prevent data loss. This is astronomically rare for SHA-256 but we handle it.
                Log($"CRITICAL: Hash collision detected for chunk {chunkHash[..8]}... - " +
                    $"will upload with collision suffix to prevent data loss. Details: {ex.Message}");
                isCollision = true;
            }

            if (!isCollision)
            {
                // Chunk verified - safe to deduplicate
                // Check if existing chunk is in a different tier than intended
                try
                {
                    var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                    var existingTier = properties.Value.AccessTier?.ToString();
                    if (existingTier != null && existingTier != storageTier.ToString())
                    {
                        Log($"WARNING: Chunk {chunkHash[..8]}... already exists in {existingTier} tier, " +
                            $"but file is configured for {storageTier} tier. Chunk will remain in {existingTier}.");
                    }
                }
                catch
                {
                    // Ignore errors checking tier - the chunk exists which is what matters
                }

                Log($"UploadChunkAsync: Chunk already exists (dedup verified), skipping upload");
                progress?.Report(encryptedData.Length);
                return blobName;
            }

            // Hash collision detected - find next available collision suffix
            blobName = await FindNextCollisionBlobNameAsync(chunkHash, cancellationToken);
            blobClient = _containerClient!.GetBlobClient(blobName);
            Log($"UploadChunkAsync: Using collision blob name: {blobName}");
        }

        // Upload with specified tier and parallel transfer options for large chunks
        BlobUploadOptions options = new()
        {
            AccessTier = ToAccessTier(storageTier),
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
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(chunkData);
        ValidateChunkHash(chunkHash);
        Log($"UploadChunkDirectAsync: Direct upload chunk {chunkHash[..8]}... ({chunkData.Length} bytes) to {storageTier} tier");

        // Encrypt the chunk before upload
        var encryptedData = _encryptionService.Encrypt(chunkData);
        
        // Use hash as blob name (content-addressable storage)
        var blobName = $"chunks/{chunkHash}";
        var blobClient = _containerClient!.GetBlobClient(blobName);

        // Upload directly without existence check - for new files this saves an API call per chunk
        BlobUploadOptions options = new()
        {
            AccessTier = ToAccessTier(storageTier),
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
    public async Task UploadFileMetadataAsync(BackedUpFile fileInfo, StorageTier storageTier = StorageTier.Hot, 
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(fileInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileInfo.LocalPath);
        Log($"UploadFileMetadataAsync: Uploading metadata for '{Path.GetFileName(fileInfo.LocalPath)}' to {storageTier} tier");

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
            AccessTier = ToAccessTier(storageTier)
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
            
            // Get blob properties to retrieve the access tier
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var accessTier = properties.Value.AccessTier;
            
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

            // Convert Azure AccessTier to our StorageTier enum
            StorageTier? storageTier = accessTier?.ToString() switch
            {
                "Hot" => StorageTier.Hot,
                "Cool" => StorageTier.Cool,
                "Cold" => StorageTier.Cold,
                _ => null
            };

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
                Status = BackupStatus.Completed,
                CurrentStorageTier = storageTier
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
    /// Uploads a generic blob (not encrypted, for system data like index backups).
    /// </summary>
    public async Task UploadBlobAsync(string blobName, byte[] data, StorageTier storageTier = StorageTier.Hot,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(data);
        
        var blobClient = _containerClient!.GetBlobClient(blobName);
        
        BlobUploadOptions options = new()
        {
            AccessTier = ToAccessTier(storageTier),
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/json"
            }
        };
        
        await using MemoryStream stream = new(data);
        await blobClient.UploadAsync(stream, options, cancellationToken);
        
        TotalOperations++;
        Log($"UploadBlobAsync: Uploaded {blobName} ({data.Length} bytes)");
    }

    /// <summary>
    /// Downloads a generic blob (not encrypted).
    /// </summary>
    public async Task<byte[]> DownloadBlobAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        
        var blobClient = _containerClient!.GetBlobClient(blobName);
        
        await using MemoryStream stream = new();
        await blobClient.DownloadToAsync(stream, cancellationToken);
        
        TotalOperations++;
        return stream.ToArray();
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

    /// <summary>
    /// Gets the properties of a blob including its storage tier and size.
    /// </summary>
    public async Task<(long sizeBytes, StorageTier tier)> GetBlobPropertiesAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        
        TotalOperations++;

        var tier = properties.Value.AccessTier?.ToString() switch
        {
            "Hot" => StorageTier.Hot,
            "Cool" => StorageTier.Cool,
            "Cold" => StorageTier.Cold,
            _ => StorageTier.Hot
        };

        return (properties.Value.ContentLength, tier);
    }

    /// <summary>
    /// Sets the storage tier for a blob.
    /// </summary>
    public async Task SetBlobTierAsync(string blobName, StorageTier tier, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);
        await blobClient.SetAccessTierAsync(ToAccessTier(tier), cancellationToken: cancellationToken);
        
        TotalOperations++;
        Log($"SetBlobTierAsync: Set {blobName} to {tier} tier");
    }

    /// <summary>
    /// Lists all chunk blobs in the container.
    /// </summary>
    public async Task<List<string>> ListChunkBlobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        List<string> chunks = new();
        
        await foreach (var blob in _containerClient!.GetBlobsAsync(prefix: "chunks/", cancellationToken: cancellationToken))
        {
            // Extract the hash from the blob name (remove "chunks/" prefix)
            var hash = blob.Name.Replace("chunks/", "");
            chunks.Add(hash);
        }
        
        TotalOperations++;
        return chunks;
    }

    /// <summary>
    /// Checks if a blob exists without downloading it.
    /// </summary>
    public async Task<bool> BlobExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);
        var exists = await blobClient.ExistsAsync(cancellationToken);
        
        TotalOperations++;
        return exists.Value;
    }

    /// <summary>
    /// Verifies that a chunk's content matches the expected data by downloading and comparing.
    /// Used for defense-in-depth verification when deduplication detects a hash match.
    /// </summary>
    public async Task<bool> VerifyChunkIntegrityAsync(string chunkHash, byte[] expectedData,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        ArgumentNullException.ThrowIfNull(expectedData);

        var blobName = $"chunks/{chunkHash}";

        try
        {
            // Download and decrypt the stored chunk
            var storedData = await DownloadChunkAsync(blobName, cancellationToken);

            // Compare sizes first (fast rejection)
            if (storedData.Length != expectedData.Length)
            {
                Log($"CRITICAL: Hash collision detected! Chunk {chunkHash[..8]}... has different sizes: " +
                    $"expected {expectedData.Length}, stored {storedData.Length}");
                throw new HashCollisionException(chunkHash, expectedData.Length, storedData.Length);
            }

            // Compare data byte-by-byte using constant-time comparison to prevent timing attacks
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedData, expectedData))
            {
                Log($"CRITICAL: Hash collision detected! Chunk {chunkHash[..8]}... has same hash but different content");
                throw new HashCollisionException(chunkHash,
                    "Content differs despite matching hash and size. This may indicate data corruption or tampering.");
            }

            Log($"VerifyChunkIntegrityAsync: Chunk {chunkHash[..8]}... verified successfully");
            return true;
        }
        catch (HashCollisionException)
        {
            throw; // Re-throw collision exceptions
        }
        catch (Exception ex)
        {
            Log($"VerifyChunkIntegrityAsync: Failed to verify chunk {chunkHash[..8]}...: {ex.Message}");
            throw new DataIntegrityException(
                $"Failed to verify chunk integrity for {chunkHash}", chunkHash, ex);
        }
    }

    /// <summary>
    /// Finds the next available blob name for a hash collision.
    /// Searches for chunks/{hash}_v2, chunks/{hash}_v3, etc.
    /// </summary>
    private async Task<string> FindNextCollisionBlobNameAsync(string chunkHash, CancellationToken cancellationToken)
    {
        // Start from _v2 (the original is just chunks/{hash})
        for (int version = 2; version <= 1000; version++) // Cap at 1000 to prevent infinite loop
        {
            var candidateName = $"chunks/{chunkHash}_v{version}";
            var blobClient = _containerClient!.GetBlobClient(candidateName);
            
            if (!await blobClient.ExistsAsync(cancellationToken))
            {
                Log($"FindNextCollisionBlobNameAsync: Found available collision name: {candidateName}");
                return candidateName;
            }
            
            TotalOperations++;
        }
        
        // This should never happen - 1000 collisions for the same hash is beyond impossible
        throw new InvalidOperationException(
            $"Exceeded maximum collision versions (1000) for hash {chunkHash}. " +
            "This indicates a serious system error.");
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
