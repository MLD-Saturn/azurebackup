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
    Task<string> UploadChunkAsync(byte[] chunkData, string chunkHash, 
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
    Task<string> UploadChunkDirectAsync(byte[] chunkData, string chunkHash, 
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
    /// Estimates monthly storage cost based on current usage.
    /// </summary>
    Task<(long totalBytes, decimal estimatedMonthlyCost)> GetStorageStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the estimated cost for operations performed.
    /// </summary>
    decimal GetEstimatedOperationsCost();

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
    Task<bool> VerifyChunkIntegrityAsync(string chunkHash, byte[] expectedData, CancellationToken cancellationToken = default);

    #endregion
}
