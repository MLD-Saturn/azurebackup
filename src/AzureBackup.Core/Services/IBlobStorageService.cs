using Azure.Core;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Interface for blob storage operations, enabling testing without Azure.
/// Supports both Entra ID and Connection String authentication.
/// </summary>
public interface IBlobStorageService : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the service is connected to storage.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Total bytes uploaded in this session.
    /// </summary>
    long TotalBytesUploaded { get; }
    
    /// <summary>
    /// Total operations performed in this session.
    /// </summary>
    int TotalOperations { get; }

    /// <summary>
    /// Cumulative count of CRC validation failures observed since the
    /// service was created. Bumped on both upload (post-encrypt CRC check)
    /// and download (pre-decrypt CRC check) paths. Operations code
    /// snapshots this around each op for <c>OperationMetrics.CrcFailCount</c>.
    /// </summary>
    long TotalCrcFailures { get; }

    /// <summary>
    /// Cumulative count of upload retries triggered by an MD5/CRC mismatch
    /// reported by the BlobClient pipeline. Distinguished from generic
    /// transient retries so a CRC regression is observable as a clean
    /// per-op delta.
    /// </summary>
    long TotalCrcRetries { get; }

    #region Connection Methods

    /// <summary>
    /// Initializes connection to blob storage using a connection string.
    /// Use this for personal Microsoft accounts.
    /// </summary>
    /// <param name="connectionString">The Azure Storage connection string</param>
    /// <param name="containerName">The container name to use for backups</param>
    Task ConnectAsync(string connectionString, string containerName);

    /// <summary>
    /// Tests the connection to blob storage using a connection string.
    /// </summary>
    Task<(bool success, string message)> TestConnectionAsync(string connectionString, string containerName);

    /// <summary>
    /// Initializes connection to blob storage using Entra ID (TokenCredential).
    /// Use this for organizational/work accounts.
    /// </summary>
    /// <param name="blobServiceUri">The blob service URI (e.g., https://account.blob.core.windows.net)</param>
    /// <param name="containerName">The container name to use for backups</param>
    /// <param name="credential">The TokenCredential for authentication</param>
    Task ConnectWithEntraIdAsync(Uri blobServiceUri, string containerName, TokenCredential credential);

    /// <summary>
    /// Tests the connection to blob storage using Entra ID.
    /// </summary>
    Task<(bool success, string message)> TestConnectionWithEntraIdAsync(Uri blobServiceUri, string containerName, TokenCredential credential);

    #endregion

    #region Blob Operations

    /// <summary>
    /// Uploads an encrypted chunk to blob storage.
    /// Checks if chunk already exists for deduplication (use for modified files).
    /// </summary>
    /// <param name="chunkData">The chunk data to upload</param>
    /// <param name="chunkHash">The hash of the chunk for content-addressable storage</param>
    /// <param name="storageTier">The Azure storage tier to use for this upload</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<string> UploadChunkAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an encrypted chunk directly without checking if it exists.
    /// Use this for new files where all chunks are guaranteed to be new.
    /// This reduces API calls by 50% for new file uploads.
    /// </summary>
    /// <param name="chunkData">The chunk data to upload</param>
    /// <param name="chunkHash">The hash of the chunk for content-addressable storage</param>
    /// <param name="storageTier">The Azure storage tier to use for this upload</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<string> UploadChunkDirectAsync(ReadOnlyMemory<byte> chunkData, string chunkHash, 
        StorageTier storageTier = StorageTier.Hot,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads file metadata (encrypted).
    /// </summary>
    /// <param name="fileInfo">The file metadata to upload</param>
    /// <param name="storageTier">The Azure storage tier to use for metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UploadFileMetadataAsync(BackedUpFile fileInfo, StorageTier storageTier = StorageTier.Hot, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a generic blob (not encrypted, for system data like index backups).
    /// </summary>
    /// <param name="blobName">The full blob name/path</param>
    /// <param name="data">The data to upload</param>
    /// <param name="storageTier">The storage tier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UploadBlobAsync(string blobName, byte[] data, StorageTier storageTier = StorageTier.Hot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a generic blob (not encrypted).
    /// </summary>
    /// <param name="blobName">The full blob name/path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<byte[]> DownloadBlobAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and decrypts a chunk.
    /// </summary>
    Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and decrypts a chunk using streaming download with pooled buffers.
    /// Uses ArrayPool for both the download and plaintext buffers, avoiding LOH pressure.
    /// The returned buffer may be oversized (rented from ArrayPool) — use Length for actual data.
    /// The caller SHOULD return the buffer via <c>ArrayPool&lt;byte&gt;.Shared.Return</c> after use.
    /// Preferred over <see cref="DownloadChunkAsync"/> for large-scale restore operations.
    /// </summary>
    Task<(byte[] Buffer, int Length)> DownloadChunkStreamingAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a chunk and attempts best-effort decryption, skipping CRC32 verification.
    /// Returns null for chunks that are completely unrecoverable (AES-GCM tag mismatch).
    /// Used for corrupted file recovery.
    /// </summary>
    Task<byte[]?> DownloadChunkBestEffortAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheap structural check on a chunk blob: returns the encrypted blob's
    /// <c>ContentLength</c> and <c>ContentHash</c> (the MD5 we set at upload
    /// time) without transferring any body bytes. This is the T1 primitive
    /// for the post-backup integrity check (D1) -- one HTTP HEAD per chunk
    /// instead of a full GET, so checking 5K unique chunks costs about
    /// 30-90 s of wall-clock and zero egress.
    /// </summary>
    /// <returns>
    /// A tuple of (Exists, ContentLength, ContentHash). When
    /// <c>Exists</c> is false the other fields are zero/null.
    /// </returns>
    Task<(bool Exists, long ContentLength, byte[]? ContentHash)> GetChunkPropertiesAsync(
        string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all backed up files by retrieving metadata blobs.
    /// </summary>
    Task<List<string>> ListMetadataBlobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and decrypts file metadata.
    /// </summary>
    Task<BackedUpFile?> DownloadFileMetadataAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a blob (used for cleanup).
    /// </summary>
    Task DeleteBlobAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the properties of a blob including its storage tier and size.
    /// </summary>
    /// <param name="blobName">The blob name (e.g., "chunks/abc123")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of size in bytes and storage tier</returns>
    Task<(long sizeBytes, StorageTier tier)> GetBlobPropertiesAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the storage tier for a blob.
    /// </summary>
    /// <param name="blobName">The blob name</param>
    /// <param name="tier">The target storage tier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetBlobTierAsync(string blobName, StorageTier tier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all chunk blobs in the container.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of chunk hashes (without the "chunks/" prefix)</returns>
    Task<List<string>> ListChunkBlobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all chunk blobs with their properties (size and tier) in a single listing call.
    /// More efficient than calling GetBlobPropertiesAsync per chunk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping chunk hash to (size, tier)</returns>
    Task<Dictionary<string, (long sizeBytes, StorageTier tier)>> ListChunkBlobsWithPropertiesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a blob exists without downloading it.
    /// </summary>
    /// <param name="blobName">The blob name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the blob exists</returns>
    Task<bool> BlobExistsAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that a chunk's content matches the expected data by downloading and comparing.
    /// Used for defense-in-depth verification when deduplication detects a hash match.
    /// </summary>
    /// <param name="chunkHash">The chunk hash (used as blob name)</param>
    /// <param name="expectedData">The expected plaintext data to compare against</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the stored chunk matches the expected data exactly</returns>
    /// <exception cref="HashCollisionException">Thrown if hash matches but data differs (extremely rare)</exception>
    Task<bool> VerifyChunkIntegrityAsync(string chunkHash, ReadOnlyMemory<byte> expectedData, CancellationToken cancellationToken = default);

    /// <summary>
    /// D6: optional callback invoked after a successful chunk upload with
    /// the chunk hash and the MD5 of the encrypted bytes that were
    /// actually stored. Used by the integrity-check engine (via
    /// <see cref="LocalDatabaseService.SetChunkExpectedMd5"/>) to capture
    /// the upload-time MD5 so future T1 checks can detect same-size
    /// post-upload corruption. Null callback = no-op (legacy behaviour).
    /// </summary>
    /// <remarks>
    /// The callback runs synchronously on the upload thread immediately
    /// after the bytes hit storage. Implementations should keep it cheap
    /// and side-effect-free with respect to the upload contract; failure
    /// in the callback should NOT roll back the upload (which already
    /// succeeded). Wiring is via the application composition root
    /// (MainWindowViewModel) where both the blob service and the
    /// database service are constructed.
    /// </remarks>
    Action<string, byte[]>? OnChunkUploaded { get; set; }

    #endregion
}
