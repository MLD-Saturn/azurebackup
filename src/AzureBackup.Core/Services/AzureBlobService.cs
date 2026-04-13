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
public partial class AzureBlobService : IBlobStorageService
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
    public async Task<string> UploadChunkAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        BlobNameValidator.ValidateChunkHash(chunkHash);
        Log($"UploadChunkAsync: Uploading chunk {chunkHash[..8]}... ({chunkData.Length} bytes) to {storageTier} tier");

        var encSize = chunkData.Length + EncryptionService.EncryptionOverhead;
        var rentedBuffer = ArrayPool<byte>.Shared.Rent(encSize);
        try
        {
            var encryptedLength = EncryptAndDiagnose(chunkData.Span, rentedBuffer, chunkHash, "UploadChunkAsync");

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
                    progress?.Report(encryptedLength);
                    return blobName;
                }

                // Hash collision detected - find next available collision suffix
                blobName = await FindNextCollisionBlobNameAsync(chunkHash, cancellationToken);
                blobClient = _containerClient!.GetBlobClient(blobName);
                Log($"UploadChunkAsync: Using collision blob name: {blobName}");
            }

            await UploadEncryptedChunkAsync(blobClient, rentedBuffer, encryptedLength,
                chunkData, chunkHash, storageTier, progress, "UploadChunkAsync", cancellationToken);

            return blobName;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: true);
        }
    }

    /// <summary>
    /// Uploads an encrypted chunk directly without checking if it exists.
    /// Use this for new files where all chunks are guaranteed to be new.
    /// This reduces API calls by 50% for new file uploads.
    /// </summary>
    public async Task<string> UploadChunkDirectAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        BlobNameValidator.ValidateChunkHash(chunkHash);
        Log($"UploadChunkDirectAsync: Direct upload chunk {chunkHash[..8]}... ({chunkData.Length} bytes) to {storageTier} tier");

        var encSize = chunkData.Length + EncryptionService.EncryptionOverhead;
        var rentedBuffer = ArrayPool<byte>.Shared.Rent(encSize);
        try
        {
            var encryptedLength = EncryptAndDiagnose(chunkData.Span, rentedBuffer, chunkHash, "UploadChunkDirectAsync");

            var blobName = $"chunks/{chunkHash}";
            var blobClient = _containerClient!.GetBlobClient(blobName);

            await UploadEncryptedChunkAsync(blobClient, rentedBuffer, encryptedLength,
                chunkData, chunkHash, storageTier, progress, "UploadChunkDirectAsync", cancellationToken);

            return blobName;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: true);
        }
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

        await UploadWithIntegrityRetryAsync(blobClient, encryptedMetadata, encryptedMetadata.Length, options,
            $"UploadFileMetadataAsync({Path.GetFileName(fileInfo.LocalPath)})",
            reEncrypt: null,
            cancellationToken);

        TotalOperations++;
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

        await UploadWithIntegrityRetryAsync(blobClient, data, data.Length, options,
            $"UploadBlobAsync({blobName})",
            reEncrypt: null,
            cancellationToken);

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
    public async Task<bool> VerifyChunkIntegrityAsync(string chunkHash, ReadOnlyMemory<byte> expectedData,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

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
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedData, expectedData.Span))
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
    /// When <paramref name="reEncrypt"/> is provided, retries re-encrypt from the
    /// original plaintext, producing a fresh nonce/ciphertext/CRC to avoid
    /// re-sending the same bytes that may have been corrupted in memory.
    /// </summary>
    /// <param name="dataLength">Actual data length within <paramref name="data"/>.
    /// May be less than data.Length when using a rented buffer from ArrayPool.</param>
    /// <param name="reEncrypt">Optional callback that re-encrypts the original plaintext
    /// into the same (or a new) buffer. Returns the buffer and actual data length.
    /// Invoked on retry attempts (attempt >= 2). Pass null for non-encrypted data.</param>
    private async Task UploadWithIntegrityRetryAsync(
        BlobClient blobClient,
        byte[] data,
        int dataLength,
        BlobUploadOptions options,
        string logContext,
        Func<(byte[] Data, int Length)>? reEncrypt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxUploadRetries; attempt++)
        {
            if (attempt > 1 && reEncrypt != null)
            {
                (data, dataLength) = reEncrypt();
                Log($"{logContext}: Re-encrypted for retry attempt {attempt}");
            }

            try
            {
                // Compute MD5 for Azure server-side verification
                options.HttpHeaders ??= new BlobHttpHeaders();
                options.HttpHeaders.ContentHash = MD5.HashData(data.AsSpan(0, dataLength));

                await using MemoryStream stream = new(data, 0, dataLength);
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

        // All retries exhausted — this is reachable only if MaxUploadRetries is 0
        throw new InvalidOperationException(
            $"{logContext}: Upload failed after {MaxUploadRetries} attempts due to repeated MD5 mismatches.");
    }

    /// <summary>
    /// Encrypts chunk data into a rented buffer and runs CRC diagnostics.
    /// Shared by <see cref="UploadChunkAsync"/> and <see cref="UploadChunkDirectAsync"/>.
    /// </summary>
    /// <returns>Number of encrypted bytes written to <paramref name="rentedBuffer"/>.</returns>
    private int EncryptAndDiagnose(ReadOnlySpan<byte> plaintext, byte[] rentedBuffer, string chunkHash, string caller)
    {
        var encryptedLength = _encryptionService.EncryptInto(plaintext, rentedBuffer);

        var crcValid = _encryptionService.ValidateCrc(rentedBuffer.AsSpan(0, encryptedLength));
        FileOperationDiagnostics.RecordChunkAmbient("Encrypt", chunkHash,
            plaintext.Length, encryptedLength, crcValid: crcValid);

        if (!crcValid)
        {
            var diag = _encryptionService.DiagnoseCrcMismatch(rentedBuffer.AsSpan(0, encryptedLength));
            FileOperationDiagnostics.RecordAmbient($"[CRC FAIL] {caller}: {diag}");
            Log($"CRITICAL: {caller}: CRC INVALID immediately after Encrypt! " +
                $"chunk={chunkHash[..8]}..., plainSize={plaintext.Length}, encSize={encryptedLength}, {diag}");
        }

        return encryptedLength;
    }

    /// <summary>
    /// Uploads an encrypted chunk with tier, transfer options, and integrity retry.
    /// Shared by <see cref="UploadChunkAsync"/> and <see cref="UploadChunkDirectAsync"/>.
    /// </summary>
    private async Task UploadEncryptedChunkAsync(
        BlobClient blobClient,
        byte[] rentedBuffer,
        int encryptedLength,
        ReadOnlyMemory<byte> originalPlaintext,
        string chunkHash,
        StorageTier storageTier,
        IProgress<long>? progress,
        string caller,
        CancellationToken cancellationToken)
    {
        BlobUploadOptions options = new()
        {
            AccessTier = ToAccessTier(storageTier),
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/octet-stream"
            },
            TransferOptions = DefaultTransferOptions
        };

        await UploadWithIntegrityRetryAsync(blobClient, rentedBuffer, encryptedLength, options,
            $"{caller}({chunkHash[..8]}...)",
            reEncrypt: () =>
            {
                var len = _encryptionService.EncryptInto(originalPlaintext.Span, rentedBuffer);
                return (rentedBuffer, len);
            },
            cancellationToken);

        TotalBytesUploaded += encryptedLength;
        TotalOperations++;
        progress?.Report(encryptedLength);
        Log($"{caller}: Chunk uploaded successfully ({encryptedLength} bytes encrypted, MD5 verified)");
    }

    /// <summary>
    /// Validates a downloaded chunk's MD5 and CRC, recording diagnostics on failure.
    /// Shared by <see cref="DownloadChunkAsync"/> and <see cref="DownloadChunkStreamingAsync"/>.
    /// </summary>
    /// <returns>True if CRC is valid.</returns>
    private bool VerifyDownloadIntegrity(
        ReadOnlySpan<byte> encryptedData,
        byte[]? storedContentHash,
        string blobName,
        string chunkHash,
        string caller)
    {
        // Verify download integrity against stored Content-MD5 (if available)
        if (storedContentHash is { Length: > 0 })
        {
            var downloadedHash = MD5.HashData(encryptedData);
            if (!downloadedHash.AsSpan().SequenceEqual(storedContentHash))
            {
                FileOperationDiagnostics.RecordChunkAmbient("MD5Fail", chunkHash,
                    0, encryptedData.Length, md5Valid: false,
                    extra: $"expected={Convert.ToHexString(storedContentHash)}, got={Convert.ToHexString(downloadedHash)}");
                Log($"{caller}: MD5 MISMATCH for {blobName}");
                throw new DataIntegrityException(
                    $"Download integrity check failed for {blobName} - data corrupted during transfer", blobName);
            }
        }

        // Diagnostic: pre-check CRC before attempting decrypt
        var crcValid = _encryptionService.ValidateCrc(encryptedData);
        var md5Valid = storedContentHash is not { Length: > 0 } || true; // passed if we got here

        FileOperationDiagnostics.RecordChunkAmbient("Decrypt", chunkHash,
            0, encryptedData.Length, crcValid: crcValid, md5Valid: md5Valid);

        if (!crcValid)
        {
            var diag = _encryptionService.DiagnoseCrcMismatch(encryptedData);
            FileOperationDiagnostics.RecordAmbient($"[CRC FAIL] {caller}: {diag}");
            Log($"DIAGNOSTIC: {caller}: CRC INVALID before decrypt! " +
                $"blob={blobName}, size={encryptedData.Length}, " +
                $"md5Verified={storedContentHash is { Length: > 0 }}, {diag}");
        }

        return crcValid;
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
