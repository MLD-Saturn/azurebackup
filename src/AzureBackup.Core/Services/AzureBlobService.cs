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

    // Retry settings for upload failures. We retry on transient failures (MD5 mismatch
    // from in-transit corruption, 5xx, 408, 429, socket / IO errors, timeouts) but NOT
    // on permanent failures (auth, not found, forbidden) which should surface immediately.
    private const int MaxUploadRetries = 10;
    private const int UploadRetryBaseDelayMs = 500;
    private const int UploadRetryMaxDelayMs = 30_000;

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

                // Hash collision on the primary blob. Before writing a new collision version,
                // probe existing _v2.._vN and dedup to an existing match if one exists. Only
                // when NO existing version matches do we create a new one — otherwise we would
                // upload a duplicate for every caller whose data hashes to the same value.
                var (dedupedBlobName, isNewCollision) = await ResolveCollisionBlobNameAsync(
                    chunkHash, chunkData, cancellationToken);
                if (!isNewCollision)
                {
                    Log($"UploadChunkAsync: Deduplicated to existing collision blob {dedupedBlobName}");
                    progress?.Report(encryptedLength);
                    return dedupedBlobName;
                }

                blobName = dedupedBlobName;
                blobClient = _containerClient!.GetBlobClient(blobName);
                Log($"UploadChunkAsync: Using new collision blob name: {blobName}");
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
            // Extract the hash from the blob name by stripping the known prefix.
            // Using an indexed slice avoids the subtle bug where string.Replace would
            // remove every occurrence of "chunks/" anywhere in the name.
            var hash = blob.Name["chunks/".Length..];
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
            // Indexed slice — see ListChunkBlobsAsync for rationale.
            var hash = blob.Name["chunks/".Length..];
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
    /// Resolves which blob name to use for a chunk whose primary hash slot is
    /// occupied by a different payload (a genuine collision, corruption, or tampering).
    /// <para>
    /// Walks the collision suffix chain <c>chunks/{hash}_v2</c>, <c>_v3</c>, …
    /// and verifies each entry against <paramref name="chunkData"/>. When an existing
    /// version matches, its blob name is returned as a dedup target. Otherwise the
    /// first unused suffix is returned for a new upload.
    /// </para>
    /// </summary>
    /// <returns>A tuple of (blobName, isNewCollision). When isNewCollision is false,
    /// the blob already exists and the caller should skip the upload.</returns>
    private async Task<(string BlobName, bool IsNewCollision)> ResolveCollisionBlobNameAsync(
        string chunkHash,
        ReadOnlyMemory<byte> chunkData,
        CancellationToken cancellationToken)
    {
        for (int version = 2; version <= 1000; version++)
        {
            var candidateName = $"chunks/{chunkHash}_v{version}";
            var blobClient = _containerClient!.GetBlobClient(candidateName);

            if (!await blobClient.ExistsAsync(cancellationToken))
            {
                Log($"ResolveCollisionBlobNameAsync: Found available collision slot: {candidateName}");
                TotalOperations++;
                return (candidateName, IsNewCollision: true);
            }

            TotalOperations++;

            // An existing collision version exists — check whether it matches our data.
            // If yes, dedup to it. If the stored content differs (hash-and-size collision
            // collision, i.e. another distinct payload on the same version), keep probing.
            try
            {
                await VerifyChunkIntegrityAsync(candidateName["chunks/".Length..], chunkData, cancellationToken);
                Log($"ResolveCollisionBlobNameAsync: Existing {candidateName} matches, deduping");
                return (candidateName, IsNewCollision: false);
            }
            catch (HashCollisionException)
            {
                // Different content at this version — keep probing.
                continue;
            }
        }

        // This should never happen — 1000 collisions for the same hash is beyond impossible
        throw new InvalidOperationException(
            $"Exceeded maximum collision versions (1000) for hash {chunkHash}. " +
            "This indicates a serious system error.");
    }

    /// <summary>
    /// Finds the next available blob name for a hash collision.
    /// Searches for chunks/{hash}_v2, chunks/{hash}_v3, etc.
    /// Kept for backward compatibility; new code should use <see cref="ResolveCollisionBlobNameAsync"/>.
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
    /// Retries transient failures (MD5 mismatch, 5xx, 408, 429, socket / IO errors,
    /// timeouts) with exponential backoff capped at <see cref="UploadRetryMaxDelayMs"/>.
    /// Permanent failures (401, 403, 404, authentication) are not retried.
    /// When <paramref name="reEncrypt"/> is provided, MD5-mismatch retries re-encrypt
    /// from the original plaintext, producing a fresh nonce/ciphertext/CRC to avoid
    /// re-sending the same bytes that may have been corrupted in memory. Non-MD5
    /// transient retries reuse the same bytes (no re-encryption cost).
    /// </summary>
    /// <param name="dataLength">Actual data length within <paramref name="data"/>.
    /// May be less than data.Length when using a rented buffer from ArrayPool.</param>
    /// <param name="reEncrypt">Optional callback that re-encrypts the original plaintext
    /// into the same (or a new) buffer. Returns the buffer and actual data length.
    /// Invoked on MD5-mismatch retries only. Pass null for non-encrypted data.</param>
    private async Task UploadWithIntegrityRetryAsync(
        BlobClient blobClient,
        byte[] data,
        int dataLength,
        BlobUploadOptions options,
        string logContext,
        Func<(byte[] Data, int Length)>? reEncrypt,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxUploadRetries; attempt++)
        {
            try
            {
                // Compute MD5 for Azure server-side verification
                options.HttpHeaders ??= new BlobHttpHeaders();
                options.HttpHeaders.ContentHash = MD5.HashData(data.AsSpan(0, dataLength));

                await using MemoryStream stream = new(data, 0, dataLength);
                await blobClient.UploadAsync(stream, options, cancellationToken);
                return; // Success
            }
            catch (RequestFailedException ex) when (IsAuthenticationFailure(ex))
            {
                // Auth/authorization failure — never retried. Wrap in a typed exception
                // so the orchestrator can invalidate cached credentials and prompt re-auth.
                Log($"{logContext}: Authentication failure (HTTP {ex.Status} {ex.ErrorCode}): {ex.Message}");
                throw new AzureAuthenticationException(
                    $"Azure rejected the request (HTTP {ex.Status} {ex.ErrorCode}). " +
                    "Credentials may be expired or lack the required permissions.",
                    ex.Status, ex.ErrorCode, ex);
            }
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                Log($"{logContext}: Azure SDK AuthenticationFailedException: {ex.Message}");
                throw new AzureAuthenticationException(
                    "Azure authentication failed — the cached credential may be expired or revoked.",
                    status: 401, errorCode: null, ex);
            }
            catch (RequestFailedException ex) when (IsPermanentFailure(ex))
            {
                // Don't retry permanent failures (auth/not-found/conflict). Surface immediately.
                Log($"{logContext}: Permanent failure (HTTP {ex.Status} {ex.ErrorCode}), not retrying: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (
                attempt < MaxUploadRetries &&
                !cancellationToken.IsCancellationRequested &&
                IsTransientFailure(ex))
            {
                lastException = ex;
                var isMd5Mismatch = ex is RequestFailedException rfe && rfe.ErrorCode == "Md5Mismatch";

                // Only re-encrypt on MD5 mismatch; other transient failures don't imply
                // corrupted bytes, so re-encrypting would be wasted CPU.
                if (isMd5Mismatch && reEncrypt != null)
                {
                    (data, dataLength) = reEncrypt();
                    Log($"{logContext}: MD5 mismatch on attempt {attempt}/{MaxUploadRetries}, re-encrypted for retry");
                }

                var delayMs = Math.Min(
                    UploadRetryBaseDelayMs * (1 << Math.Min(attempt - 1, 10)),
                    UploadRetryMaxDelayMs);
                var reason = isMd5Mismatch
                    ? "MD5 mismatch"
                    : ex is RequestFailedException rf
                        ? $"HTTP {rf.Status} {rf.ErrorCode}"
                        : ex.GetType().Name;
                Log($"{logContext}: Transient failure ({reason}) on attempt {attempt}/{MaxUploadRetries}, " +
                    $"retrying in {delayMs}ms...");
                FileOperationDiagnostics.RecordAmbient(
                    $"[RETRY] {logContext} attempt={attempt}/{MaxUploadRetries} reason={reason} delayMs={delayMs}");
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        var message = $"{logContext}: Upload failed after {MaxUploadRetries} attempts.";
        if (lastException != null)
        {
            throw new IOException(
                message + $" Last error: {lastException.GetType().Name}: {lastException.Message}",
                lastException);
        }
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Classifies an Azure request failure as an authentication / authorization failure.
    /// </summary>
    private static bool IsAuthenticationFailure(RequestFailedException ex)
        => ex.Status is 401 or 403;

    /// <summary>
    /// Classifies an Azure upload failure as permanent (do not retry).
    /// Permanent failures include not found, conflict, and bad request (non-MD5).
    /// Authentication failures (401/403) are handled separately so callers can
    /// invalidate cached credentials rather than treating them as generic errors.
    /// </summary>
    private static bool IsPermanentFailure(RequestFailedException ex)
    {
        // MD5 mismatch is transient (in-transit corruption), even though it arrives as HTTP 400
        if (ex.ErrorCode == "Md5Mismatch") return false;
        // 401/403 are handled by IsAuthenticationFailure before this is reached
        return ex.Status is 400 or 404 or 409 or 412;
    }

    /// <summary>
    /// Classifies an exception as a transient failure worth retrying.
    /// </summary>
    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        RequestFailedException rfe =>
            rfe.ErrorCode == "Md5Mismatch" ||
            rfe.Status == 0 ||                    // no HTTP status = network error
            rfe.Status == 408 ||                  // request timeout
            rfe.Status == 429 ||                  // throttled
            rfe.Status >= 500,                    // server error
        TimeoutException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        System.Net.Http.HttpRequestException => true,
        // Task cancellation that was NOT caused by the user's token = server-side timeout
        TaskCanceledException tc when !tc.CancellationToken.IsCancellationRequested => true,
        _ => false
    };

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
