using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// In-memory implementation of IBlobStorageService for testing without Azure.
/// Simulates blob storage behavior including deduplication, metadata storage,
/// and security validation matching AzureBlobService behavior.
/// Supports both connection string and Entra ID authentication (simulated).
/// </summary>
public class InMemoryBlobService : IBlobStorageService
{
    private readonly EncryptionService _encryptionService;
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<string, StorageTier> _blobTiers = new();
    private bool _isConnected;
    
    // Simulated latency for more realistic testing (milliseconds)
    private readonly int _simulatedLatencyMs;
    
    // Simulated failure rate for resilience testing (0.0 to 1.0)
    private readonly double _failureRate;
    
    // Thread-safe random number generator lock
    private readonly object _randomLock = new();
    private readonly Random _random = new();

    public bool IsConnected => _isConnected;

    private long _totalBytesUploaded;
    private int _totalOperations;

    public long TotalBytesUploaded => Interlocked.Read(ref _totalBytesUploaded);
    public int TotalOperations => Volatile.Read(ref _totalOperations);
    
    /// <summary>
    /// Gets all stored blob names (for test verification).
    /// </summary>
    public IReadOnlyCollection<string> StoredBlobNames => _blobs.Keys.ToList();
    
    /// <summary>
    /// Gets the total storage used (for test verification).
    /// </summary>
    public long TotalStorageUsed => _blobs.Values.Sum(b => (long)b.Length);

    public InMemoryBlobService(EncryptionService encryptionService, int simulatedLatencyMs = 0, double failureRate = 0.0)
    {
        ArgumentNullException.ThrowIfNull(encryptionService);
        _encryptionService = encryptionService;
        _simulatedLatencyMs = simulatedLatencyMs;
        _failureRate = Math.Clamp(failureRate, 0.0, 1.0);
    }

    #region Connection String Authentication

    public Task ConnectAsync(string connectionString, string containerName)
    {
        // For in-memory, we just validate inputs and mark as connected
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task<(bool success, string message)> TestConnectionAsync(string connectionString, string containerName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return Task.FromResult((false, "Connection string is required"));
        if (string.IsNullOrWhiteSpace(containerName))
            return Task.FromResult((false, "Container name is required"));
            
        return Task.FromResult((true, "In-memory connection successful"));
    }

    #endregion

    #region Entra ID Authentication

    public Task ConnectWithEntraIdAsync(Uri blobServiceUri, string containerName, TokenCredential credential)
    {
        // For in-memory, we just validate inputs and mark as connected
        ArgumentNullException.ThrowIfNull(blobServiceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(credential);
        
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task<(bool success, string message)> TestConnectionWithEntraIdAsync(
        Uri blobServiceUri, string containerName, TokenCredential credential)
    {
        if (blobServiceUri == null)
            return Task.FromResult((false, "Blob service URI is required"));
        if (string.IsNullOrWhiteSpace(containerName))
            return Task.FromResult((false, "Container name is required"));
        if (credential == null)
            return Task.FromResult((false, "Credential is required"));
            
        return Task.FromResult((true, "In-memory connection successful (Entra ID simulated)"));
    }


    #endregion

    #region Blob Operations

    public virtual async Task<string> UploadChunkAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        BlobNameValidator.ValidateChunkHash(chunkHash);

        await SimulateLatencyAsync(cancellationToken);
        SimulateFailure("Upload chunk");

        var blobName = $"chunks/{chunkHash}";

        // Check for deduplication
        if (_blobs.ContainsKey(blobName))
        {
            // Defense-in-depth: Verify the stored chunk actually matches our data
            bool isCollision = false;
            try
            {
                await VerifyChunkIntegrityAsync(chunkHash, chunkData, cancellationToken);
            }
            catch (HashCollisionException)
            {
                // CRITICAL: Hash collision detected - upload with collision suffix to prevent data loss
                isCollision = true;
            }

            if (!isCollision)
            {
                // Chunk verified - safe to deduplicate
                progress?.Report(chunkData.Length);
                return blobName;
            }

            // Hash collision on primary slot - find or create the appropriate _vN blob.
            var (resolvedName, isNewCollision) = ResolveCollisionBlobName(chunkHash, chunkData);
            if (!isNewCollision)
            {
                // An existing collision version matches our data; dedup to it.
                progress?.Report(chunkData.Length);
                return resolvedName;
            }

            blobName = resolvedName;
        }

        // Encrypt and store
        var encryptedData = _encryptionService.Encrypt(chunkData.Span);
        _blobs[blobName] = encryptedData;
        _blobTiers[blobName] = storageTier;
        
        Interlocked.Add(ref _totalBytesUploaded, encryptedData.Length);
        Interlocked.Increment(ref _totalOperations);
        progress?.Report(encryptedData.Length);

        return blobName;
    }

    /// <summary>
    /// Uploads an encrypted chunk directly without checking if it exists.
    /// For InMemoryBlobService, this behaves the same as UploadChunkAsync but skips the dedup check.
    /// </summary>
    public virtual async Task<string> UploadChunkDirectAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        BlobNameValidator.ValidateChunkHash(chunkHash);

        await SimulateLatencyAsync(cancellationToken);
        SimulateFailure("Upload chunk direct");

        var blobName = $"chunks/{chunkHash}";

        // Direct upload - no deduplication check (for new files)
        var encryptedData = _encryptionService.Encrypt(chunkData.Span);
        _blobs[blobName] = encryptedData;
        _blobTiers[blobName] = storageTier;
        
        Interlocked.Add(ref _totalBytesUploaded, encryptedData.Length);
        Interlocked.Increment(ref _totalOperations);
        progress?.Report(encryptedData.Length);

        return blobName;
    }

    public async Task UploadFileMetadataAsync(BackedUpFile fileInfo, StorageTier storageTier = StorageTier.Hot, 
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(fileInfo);
        
        await SimulateLatencyAsync(cancellationToken);
        SimulateFailure("Upload metadata");

        var metadata = JsonSerializer.Serialize(new
        {
            fileInfo.LocalPath,
            fileInfo.FileSize,
            fileInfo.LastModified,
            fileInfo.FileHash,
            Chunks = fileInfo.Chunks.Select(c => new { c.Index, c.Hash, c.Offset, c.Length }),
            fileInfo.MetadataVersion
        });
        
        var encryptedMetadata = _encryptionService.Encrypt(System.Text.Encoding.UTF8.GetBytes(metadata));

        var metadataHash = _encryptionService.ComputeHmacHex(fileInfo.LocalPath);
        var blobName = $"metadata/{metadataHash}";
        
        _blobs[blobName] = encryptedMetadata;
        Interlocked.Increment(ref _totalOperations);
    }

    public virtual async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        // Validate blob name format - must start with "chunks/" (same as AzureBlobService)
        if (!blobName.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        await SimulateLatencyAsync(cancellationToken);
        SimulateFailure("Download chunk");

        if (!_blobs.TryGetValue(blobName, out var encryptedData))
        {
            throw new DataIntegrityException($"Chunk not found: {blobName}", blobName);
        }

        Interlocked.Increment(ref _totalOperations);
        return _encryptionService.Decrypt(encryptedData);
    }

    /// <summary>
    /// Streaming download variant — in-memory implementation delegates to <see cref="DownloadChunkAsync"/>
    /// since there is no actual I/O stream to optimize.
    /// Copies into a rented buffer so the consumer can safely call ArrayPool.Return.
    /// </summary>
    public virtual async Task<(byte[] Buffer, int Length)> DownloadChunkStreamingAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var data = await DownloadChunkAsync(blobName, cancellationToken);
        var rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented, 0);
        return (rented, data.Length);
    }

    /// <summary>
    /// Downloads a chunk with best-effort decryption (skips CRC32 check).
    /// Returns null for unrecoverable chunks.
    /// </summary>
    public virtual async Task<byte[]?> DownloadChunkBestEffortAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        if (!blobName.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        await SimulateLatencyAsync(cancellationToken);

        if (!_blobs.TryGetValue(blobName, out var encryptedData))
            return null;

        Interlocked.Increment(ref _totalOperations);
        return _encryptionService.DecryptBestEffort(encryptedData);
    }

    public Task<List<string>> ListMetadataBlobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        
        var metadataBlobs = _blobs.Keys
            .Where(k => k.StartsWith("metadata/"))
            .ToList();
        
        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(metadataBlobs);
    }

    public async Task<BackedUpFile?> DownloadFileMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        
        await SimulateLatencyAsync(cancellationToken);

        if (!_blobs.TryGetValue(blobName, out var encryptedData))
        {
            return null;
        }

        try
        {
            var decryptedData = _encryptionService.Decrypt(encryptedData);
            var json = System.Text.Encoding.UTF8.GetString(decryptedData);
            
            var metadata = JsonSerializer.Deserialize<MetadataDto>(json);
            if (metadata == null) return null;

            Interlocked.Increment(ref _totalOperations);
            
            return new BackedUpFile
            {
                LocalPath = metadata.LocalPath,
                FileSize = metadata.FileSize,
                LastModified = metadata.LastModified,
                FileHash = metadata.FileHash,
                MetadataVersion = metadata.Version,
                Chunks = metadata.Chunks.Select(c => new ChunkInfo
                {
                    Index = c.Index,
                    Hash = c.Hash,
                    Offset = c.Offset,
                    Length = c.Length,
                    BlobName = $"chunks/{c.Hash}"
                }).ToList(),
                Status = BackupStatus.Completed,
                // For in-memory service, simulate Hot tier as default
                CurrentStorageTier = StorageTier.Hot
            };
        }
        catch (Exception ex) when (ex is not DataIntegrityException)
        {
            throw new DataIntegrityException($"Failed to read metadata: {blobName}", blobName, ex);
        }
    }

    public Task DeleteBlobAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        
        _blobs.TryRemove(blobName, out _);
        _blobTiers.TryRemove(blobName, out _);
        Interlocked.Increment(ref _totalOperations);
        
        return Task.CompletedTask;
    }

    public Task UploadBlobAsync(string blobName, byte[] data, StorageTier storageTier = StorageTier.Hot,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(data);
        
        // Store raw data (not encrypted) for generic blobs
        _blobs[blobName] = data;
        _blobTiers[blobName] = storageTier;
        Interlocked.Increment(ref _totalOperations);
        
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadBlobAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        
        if (!_blobs.TryGetValue(blobName, out var data))
        {
            throw new InvalidOperationException($"Blob not found: {blobName}");
        }
        
        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(data);
    }

    public Task<(long sizeBytes, StorageTier tier)> GetBlobPropertiesAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        if (!_blobs.TryGetValue(blobName, out var data))
        {
            throw new InvalidOperationException($"Blob not found: {blobName}");
        }

        // Get tier from our tracking dictionary, default to Cool
        var tier = _blobTiers.GetValueOrDefault(blobName, StorageTier.Hot);
        
        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(((long)data.Length, tier));
    }

    public Task SetBlobTierAsync(string blobName, StorageTier tier, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        if (!_blobs.ContainsKey(blobName))
        {
            throw new InvalidOperationException($"Blob not found: {blobName}");
        }

        _blobTiers[blobName] = tier;
        Interlocked.Increment(ref _totalOperations);
        return Task.CompletedTask;
    }

    public Task<List<string>> ListChunkBlobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var chunks = _blobs.Keys
            .Where(k => k.StartsWith("chunks/"))
            .Select(k => k.Replace("chunks/", ""))
            .ToList();

        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(chunks);
    }

    /// <summary>
    /// Lists all chunk blobs with their properties (size and tier).
    /// </summary>
    public Task<Dictionary<string, (long sizeBytes, StorageTier tier)>> ListChunkBlobsWithPropertiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var result = new Dictionary<string, (long, StorageTier)>(StringComparer.Ordinal);

        foreach (var (key, data) in _blobs)
        {
            if (!key.StartsWith("chunks/")) continue;

            var hash = key.Replace("chunks/", "");
            var tier = _blobTiers.GetValueOrDefault(key, StorageTier.Hot);
            result[hash] = (data.Length, tier);
        }

        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(result);
    }

    public Task<bool> BlobExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        Interlocked.Increment(ref _totalOperations);
        return Task.FromResult(_blobs.ContainsKey(blobName));
    }

    /// <summary>
    /// Verifies that a chunk's content matches the expected data by downloading and comparing.
    /// Used for defense-in-depth verification when deduplication detects a hash match.
    /// </summary>
    public Task<bool> VerifyChunkIntegrityAsync(string chunkHash, ReadOnlyMemory<byte> expectedData,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        var blobName = $"chunks/{chunkHash}";

        if (!_blobs.TryGetValue(blobName, out var encryptedData))
        {
            throw new InvalidOperationException($"Chunk not found: {chunkHash}");
        }

        try
        {
            // Decrypt the stored chunk
            var storedData = _encryptionService.Decrypt(encryptedData);

            // Compare sizes first (fast rejection)
            if (storedData.Length != expectedData.Length)
            {
                throw new HashCollisionException(chunkHash, expectedData.Length, storedData.Length);
            }

            // Compare data byte-by-byte using constant-time comparison
            if (!CryptographicOperations.FixedTimeEquals(storedData, expectedData.Span))
            {
                throw new HashCollisionException(chunkHash,
                    "Content differs despite matching hash and size. This may indicate data corruption or tampering.");
            }

            Interlocked.Increment(ref _totalOperations);
            return Task.FromResult(true);
        }
        catch (HashCollisionException)
        {
            throw; // Re-throw collision exceptions
        }
        catch (Exception ex)
        {
            throw new DataIntegrityException(
                $"Failed to verify chunk integrity for {chunkHash}", chunkHash, ex);
        }
    }

    /// <summary>
    /// Resolves the collision blob name for a chunk whose primary slot holds
    /// different data. Mirrors <c>AzureBlobService.ResolveCollisionBlobNameAsync</c>:
    /// enumerates existing <c>_v2.._vN</c> entries, dedups to any that matches
    /// <paramref name="chunkData"/>, otherwise returns the smallest unused suffix.
    /// </summary>
    private (string BlobName, bool IsNewCollision) ResolveCollisionBlobName(
        string chunkHash,
        ReadOnlyMemory<byte> chunkData)
    {
        var prefix = $"chunks/{chunkHash}_v";
        var existingVersions = new SortedDictionary<int, string>();

        foreach (var key in _blobs.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var versionText = key[prefix.Length..];
            if (int.TryParse(versionText, out var version) && version >= 2)
            {
                existingVersions[version] = key;
            }
        }

        // Verify each existing version; dedup on match.
        foreach (var (_, existingName) in existingVersions)
        {
            try
            {
                // Inline comparison (no async needed in-memory).
                var storedEncrypted = _blobs[existingName];
                var stored = _encryptionService.Decrypt(storedEncrypted);
                if (stored.Length == chunkData.Length &&
                    CryptographicOperations.FixedTimeEquals(stored, chunkData.Span))
                {
                    return (existingName, IsNewCollision: false);
                }
            }
            catch
            {
                // Treat any decryption / compare error as a non-match and keep checking.
            }
        }

        // No match — pick the smallest unused version >= 2.
        var nextVersion = 2;
        foreach (var version in existingVersions.Keys)
        {
            if (version != nextVersion) break;
            nextVersion++;
        }

        if (nextVersion > 1000)
        {
            throw new InvalidOperationException(
                $"Exceeded maximum collision versions (1000) for hash {chunkHash}.");
        }

        return ($"chunks/{chunkHash}_v{nextVersion}", IsNewCollision: true);
    }

    /// <summary>
    /// Legacy helper retained for reference; superseded by <see cref="ResolveCollisionBlobName"/>.
    /// </summary>
    private string FindNextCollisionBlobName(string chunkHash)
    {
        for (int version = 2; version <= 1000; version++)
        {
            var candidateName = $"chunks/{chunkHash}_v{version}";
            if (!_blobs.ContainsKey(candidateName))
            {
                return candidateName;
            }
        }

        throw new InvalidOperationException(
            $"Exceeded maximum collision versions (1000) for hash {chunkHash}.");
    }

    #endregion

    public ValueTask DisposeAsync()
    {
        _blobs.Clear();
        _isConnected = false;
        return ValueTask.CompletedTask;
    }
    
    /// <summary>
    /// Clears all stored data (for test cleanup).
    /// </summary>
    public void Clear()
    {
        _blobs.Clear();
        _blobTiers.Clear();
        _totalBytesUploaded = 0;
        _totalOperations = 0;
    }
    
    /// <summary>
    /// Gets raw encrypted blob data (for test verification).
    /// </summary>
    public byte[]? GetRawBlob(string blobName)
    {
        _blobs.TryGetValue(blobName, out var data);
        return data;
    }

    private void EnsureConnected()
    {
        if (!_isConnected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync or ConnectWithEntraIdAsync first.");
    }

    private async Task SimulateLatencyAsync(CancellationToken cancellationToken)
    {
        if (_simulatedLatencyMs > 0)
        {
            await Task.Delay(_simulatedLatencyMs, cancellationToken);
        }
    }

    private void SimulateFailure(string operation)
    {
        if (_failureRate > 0)
        {
            double randomValue;
            lock (_randomLock)
            {
                randomValue = _random.NextDouble();
            }
            
            if (randomValue < _failureRate)
            {
                throw new InvalidOperationException($"Simulated failure during: {operation}");
            }
        }
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
