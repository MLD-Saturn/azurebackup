using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
public class AzureBlobService : IBlobStorageService
{
    private BlobServiceClient? _serviceClient;
    private BlobContainerClient? _containerClient;
    private readonly EncryptionService _encryptionService;
    

    // Transfer optimization settings
    // These settings maximize bandwidth by using parallel block transfers within each blob
    private static readonly StorageTransferOptions DefaultTransferOptions = new()
    {
        MaximumConcurrency = 8,              // Parallel block uploads/downloads per blob
        MaximumTransferSize = 8 * 1024 * 1024,  // 8 MB blocks for large files
        InitialTransferSize = 8 * 1024 * 1024   // Start with 8 MB initial transfer
    };

    // Retry settings for upload integrity failures (MD5 mismatch from Azure)
    private const int MaxUploadRetries = 25;
    private const int UploadRetryBaseDelayMs = 500;

    public bool IsConnected => _containerClient != null;
    public long TotalBytesUploaded { get; private set; }
    public int TotalOperations { get; private set; }
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    [Conditional("DIAGNOSTICLOG")]
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
        StorageTier.Archive => AccessTier.Archive,
        _ => AccessTier.Cool // Default to Cool for unknown values
    };

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
        BlobNameValidator.ValidateChunkHash(chunkHash);
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
                // Chunk verified — safe to deduplicate
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

        await UploadWithIntegrityRetryAsync(blobClient, encryptedData, options,
            $"UploadChunkAsync({chunkHash[..8]}...)", cancellationToken);

        TotalBytesUploaded += encryptedData.Length;
        TotalOperations++;
        progress?.Report(encryptedData.Length);
        Log($"UploadChunkAsync: Chunk uploaded successfully ({encryptedData.Length} bytes encrypted, MD5 verified)");

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
        BlobNameValidator.ValidateChunkHash(chunkHash);
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

        await UploadWithIntegrityRetryAsync(blobClient, encryptedData, options,
            $"UploadChunkDirectAsync({chunkHash[..8]}...)", cancellationToken);

        TotalBytesUploaded += encryptedData.Length;
        TotalOperations++;
        progress?.Report(encryptedData.Length);
        Log($"UploadChunkDirectAsync: Chunk uploaded successfully ({encryptedData.Length} bytes encrypted, MD5 verified)");

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

        // Use HMAC-SHA256 keyed by the derived encryption key for deterministic blob naming.
        // This prevents an attacker with storage access from confirming file paths
        // via dictionary attack (plain SHA-256 of a path is guessable).
        var metadataHash = _encryptionService.ComputeHmacHex(fileInfo.LocalPath);
        var blobName = $"metadata/{metadataHash}";
        
        var blobClient = _containerClient!.GetBlobClient(blobName);

        BlobUploadOptions options = new()
        {
            AccessTier = ToAccessTier(storageTier)
        };

        await UploadWithIntegrityRetryAsync(blobClient, encryptedMetadata, options,
            $"UploadFileMetadataAsync({Path.GetFileName(fileInfo.LocalPath)})", cancellationToken);

        TotalOperations++;
    }

    /// <summary>
    /// Downloads and decrypts a chunk. Uses a single API call to retrieve both
    /// content and Content-MD5 hash for transport integrity verification.
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
            // Single API call — returns both content and Content-MD5 in one HTTP response
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var encryptedData = response.Value.Content.ToArray();
            var storedContentHash = response.Value.Details.ContentHash;
            TotalOperations++;

            // Verify download integrity against stored Content-MD5 (if available)
            if (storedContentHash is { Length: > 0 })
            {
                var downloadedHash = MD5.HashData(encryptedData);
                if (!downloadedHash.AsSpan().SequenceEqual(storedContentHash))
                {
                    Log($"DownloadChunkAsync: MD5 MISMATCH for {blobName} - data corrupted during download " +
                        $"(expected={Convert.ToHexString(storedContentHash)}, got={Convert.ToHexString(downloadedHash)})");
                    throw new DataIntegrityException(
                        $"Download integrity check failed for {blobName} - data corrupted during transfer", blobName);
                }
            }

            if (encryptedData.Length > 0)
            {
                Log($"DownloadChunkAsync: Downloaded {blobName} ({encryptedData.Length:N0} bytes encrypted), " +
                    $"GC.TotalMemory={GC.GetTotalMemory(false):N0}, decrypting...");
            }
            else
            {
                Log($"DownloadChunkAsync: Downloaded {blobName} ({encryptedData.Length} bytes encrypted), decrypting...");
            }
            try
            {
                var decrypted = _encryptionService.Decrypt(encryptedData);
                if (encryptedData.Length > 0)
                {
                    Log($"DownloadChunkAsync: Decrypted {blobName}: {encryptedData.Length:N0} -> {decrypted.Length:N0} bytes, " +
                        $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                }
                return decrypted;
            }
            catch (Exception ex)
            {
                Log($"DownloadChunkAsync: DECRYPT FAILED for {blobName} " +
                    $"(encryptedSize={encryptedData.Length:N0}): {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Log($"DownloadChunkAsync: Chunk not found (404): {blobName}");
            throw new DataIntegrityException($"Chunk not found: {blobName}", blobName, ex);
        }
        catch (RequestFailedException ex)
        {
            Log($"DownloadChunkAsync: Azure request failed for {blobName}: HTTP {ex.Status} - {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is not DataIntegrityException and not SecurityPolicyException)
        {
            Log($"DownloadChunkAsync: EXCEPTION for {blobName}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Downloads a chunk and attempts best-effort decryption, skipping CRC32 verification.
    /// Returns null for chunks that are completely unrecoverable.
    /// Used for corrupted file recovery to __corrupted__ subfolder.
    /// </summary>
    public async Task<byte[]?> DownloadChunkBestEffortAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        if (!blobName.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);

        try
        {
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var encryptedData = response.Value.Content.ToArray();
            TotalOperations++;

            var decrypted = _encryptionService.DecryptBestEffort(encryptedData);
            if (decrypted != null)
            {
                Log($"DownloadChunkBestEffortAsync: Recovered {blobName} ({encryptedData.Length:N0} -> {decrypted.Length:N0} bytes)");
            }
            else
            {
                Log($"DownloadChunkBestEffortAsync: {blobName} is unrecoverable (AES-GCM tag mismatch)");
            }
            return decrypted;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Log($"DownloadChunkBestEffortAsync: Chunk not found: {blobName}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"DownloadChunkBestEffortAsync: Failed for {blobName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads and decrypts a chunk using parallel range downloads for improved throughput.
    /// Uses <see cref="StorageTransferOptions"/> for 8-way parallel block downloads within each chunk,
    /// and <see cref="ArrayPool{T}"/> to rent the download buffer, avoiding LOH allocations.
    /// The returned decrypted data is a regular array (not pooled) since it is passed to the channel consumer.
    /// </summary>
    public async Task<byte[]> DownloadChunkStreamingAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        if (!blobName.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);

        try
        {
            // Use DownloadToAsync with StorageTransferOptions for parallel range downloads.
            // This enables 8-way concurrent block downloads within a single chunk,
            // significantly improving throughput for chunks > 8 MB.
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var contentLength = properties.Value.ContentLength;
            var storedContentHash = properties.Value.ContentHash;
            TotalOperations++;

            // Rent buffer from pool — avoids LOH allocation for large chunks
            var rentedBuffer = ArrayPool<byte>.Shared.Rent((int)contentLength);
            try
            {
                // Download with parallel ranges into a MemoryStream backed by the rented buffer
                using var memoryStream = new MemoryStream(rentedBuffer, 0, (int)contentLength, writable: true);
                var downloadOptions = new BlobDownloadToOptions
                {
                    TransferOptions = DefaultTransferOptions
                };
                await blobClient.DownloadToAsync(memoryStream, downloadOptions, cancellationToken);
                var bytesRead = (int)memoryStream.Position;

                // Verify download integrity against stored Content-MD5 (if available)
                if (storedContentHash is { Length: > 0 })
                {
                    var downloadedHash = MD5.HashData(rentedBuffer.AsSpan(0, bytesRead));
                    if (!downloadedHash.AsSpan().SequenceEqual(storedContentHash))
                    {
                        Log($"DownloadChunkStreamingAsync: MD5 MISMATCH for {blobName}");
                        throw new DataIntegrityException(
                            $"Download integrity check failed for {blobName} - data corrupted during transfer", blobName);
                    }
                }

                Log($"DownloadChunkStreamingAsync: Downloaded {blobName} ({bytesRead:N0} bytes encrypted), decrypting...");

                // Decrypt using the exact slice (not the full rented buffer)
                var decrypted = _encryptionService.Decrypt(rentedBuffer.AsSpan(0, bytesRead));

                Log($"DownloadChunkStreamingAsync: Decrypted {blobName}: {bytesRead:N0} -> {decrypted.Length:N0} bytes");
                return decrypted;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Log($"DownloadChunkStreamingAsync: Chunk not found (404): {blobName}");
            throw new DataIntegrityException($"Chunk not found: {blobName}", blobName, ex);
        }
        catch (RequestFailedException ex)
        {
            Log($"DownloadChunkStreamingAsync: Azure request failed for {blobName}: HTTP {ex.Status} - {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is not DataIntegrityException and not SecurityPolicyException)
        {
            Log($"DownloadChunkStreamingAsync: EXCEPTION for {blobName}: {ex.GetType().Name}: {ex.Message}");
            throw;
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
            
            // Single API call to get content (faster than GetProperties + DownloadTo)
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            
            var encryptedData = response.Value.Content.ToArray();
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

            // Storage tier is not retrieved in single-call mode (would require separate GetProperties)
            // This is acceptable since tier info is optional for metadata listing
            StorageTier? storageTier = null;

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
    /// Uploads a generic blob to storage. SECURITY: This method does NOT encrypt data.
    /// Callers MUST encrypt data before calling this method to maintain zero-knowledge.
    /// Currently only used by ChunkIndexService which encrypts before calling.
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

        await UploadWithIntegrityRetryAsync(blobClient, data, options,
            $"UploadBlobAsync({blobName})", cancellationToken);

        TotalOperations++;
        Log($"UploadBlobAsync: Uploaded {blobName} ({data.Length} bytes, MD5 verified)");
    }

    /// <summary>
    /// Downloads a generic blob from storage. SECURITY: This method does NOT decrypt data.
    /// Callers MUST decrypt data after calling this method.
    /// Currently only used by ChunkIndexService which decrypts after calling.
    /// </summary>
    public async Task<byte[]> DownloadBlobAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var blobClient = _containerClient!.GetBlobClient(blobName);

        var response = await blobClient.DownloadContentAsync(cancellationToken);

        TotalOperations++;
        return response.Value.Content.ToArray();
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
    /// Lists all chunk blobs with their properties in a single listing call.
    /// Azure's GetBlobsAsync returns ContentLength and AccessTier in the listing response,
    /// eliminating the need for individual GetProperties calls.
    /// </summary>
    public async Task<Dictionary<string, (long sizeBytes, StorageTier tier)>> ListChunkBlobsWithPropertiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var result = new Dictionary<string, (long, StorageTier)>(StringComparer.Ordinal);

        await foreach (var blob in _containerClient!.GetBlobsAsync(prefix: "chunks/", cancellationToken: cancellationToken))
        {
            var hash = blob.Name.Replace("chunks/", "");
            var tier = blob.Properties.AccessTier?.ToString() switch
            {
                "Hot" => StorageTier.Hot,
                "Cool" => StorageTier.Cool,
                "Cold" => StorageTier.Cold,
                _ => StorageTier.Hot
            };
            result[hash] = (blob.Properties.ContentLength ?? 0, tier);
        }

        TotalOperations++;
        Log($"ListChunkBlobsWithPropertiesAsync: Listed {result.Count} chunks with properties");
        return result;
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

    /// <summary>
    /// Uploads data to a blob with MD5 integrity verification and automatic retry.
    /// On MD5 mismatch (transit corruption), retries with exponential backoff.
    /// For encrypted data, the caller should pass the plaintext and this method
    /// re-encrypts on each retry (producing a fresh nonce/ciphertext to avoid
    /// re-sending the same bytes that were corrupted).
    /// </summary>
    private async Task UploadWithIntegrityRetryAsync(
        BlobClient blobClient,
        byte[] data,
        BlobUploadOptions options,
        string logContext,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxUploadRetries; attempt++)
        {
            try
            {
                // Compute MD5 for Azure server-side verification
                options.HttpHeaders ??= new BlobHttpHeaders();
                options.HttpHeaders.ContentHash = MD5.HashData(data);

                await using MemoryStream stream = new(data);
                await blobClient.UploadAsync(stream, options, cancellationToken);
                return; // Success
            }
            catch (RequestFailedException ex) when (
                attempt < MaxUploadRetries &&
                !cancellationToken.IsCancellationRequested &&
                (ex.ErrorCode == "Md5Mismatch" || ex.Status == 400))
            {
                var delayMs = UploadRetryBaseDelayMs * (1 << (attempt - 1)); // Exponential backoff
                Log($"{logContext}: MD5 mismatch on attempt {attempt}/{MaxUploadRetries}, " +
                    $"data corrupted in transit. Retrying in {delayMs}ms...");
                await Task.Delay(delayMs, cancellationToken);
            }
        }
    }

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
