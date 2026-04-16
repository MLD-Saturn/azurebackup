using System.Buffers;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Threading.Channels;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Implements Content-Defined Chunking (CDC) using a rolling hash (Rabin fingerprint style).
/// This enables efficient delta sync by splitting files into variable-sized chunks based on content.
/// When a file changes, only the modified chunks need to be uploaded.
/// Chunk sizes are adaptive based on file type for optimal performance and cost.
/// Uses SHA-256 for chunk hashes to provide collision-resistant content addressing.
/// </summary>
public class ChunkingService
{
    #region Chunk Size Constants

    // Size constants for readability
    private const int KB = 1024;
    private const int MB = KB * KB;

    // Default chunking parameters
    private const int DefaultMinChunkSize = 64 * KB;       // 64 KB minimum chunk
    private const int DefaultMaxChunkSize = 1 * MB;        // 1 MB maximum chunk

    // Chunk mask bits explanation:
    // The mask determines average chunk size: average = 2^(mask bits) bytes
    // When (rollingHash & mask) == 0, a chunk boundary is found
    // More bits = larger average chunks, fewer bits = smaller average chunks
    private const int DefaultMaskBits = 18;                           // 2^18 = 256 KB average
    private const uint DefaultChunkMask = (1u << DefaultMaskBits) - 1; // 0x0003FFFF

    // Mask bit constants for different file types
    private const int SmallChunkMaskBits = 16;   // 2^16 = 64 KB average (text/code)
    private const int MediumChunkMaskBits = 17;  // 2^17 = 128 KB average (documents)
    private const int LargeChunkMaskBits = 20;   // 2^20 = 1 MB average (images)
    private const int XLargeChunkMaskBits = 21;  // 2^21 = 2 MB average (audio)
    private const int XXLargeChunkMaskBits = 22; // 2^22 = 4 MB average (video)
    private const int HugeChunkMaskBits = 23;    // 2^23 = 8 MB average (large video/ISO)
    private const int GiantChunkMaskBits = 26;   // 2^26 = 64 MB average (very large files)

    #endregion

    // Rolling hash parameters (Rabin-like)
    private const uint HashPrime = 31;
    private const int WindowSize = 48;

    // Precomputed: HashPrime^(WindowSize-1) = 31^47 mod 2^32
    private const uint HashPrimePower = 969_581_023;

    /// <summary>
    /// Chunk size configuration by file category.
    /// </summary>
    private record ChunkSizeConfig(int MinChunkSize, int MaxChunkSize, uint ChunkMask);

    /// <summary>
    /// Creates a chunk mask from bit count.
    /// </summary>
    private static uint MaskFromBits(int bits) => (1u << bits) - 1;

    /// <summary>
    /// File extensions mapped to chunking configurations.
    /// Mask bits determine average chunk size: 2^bits bytes average.
    /// </summary>
    private static readonly FrozenDictionary<string, ChunkSizeConfig> ChunkConfigByExtension = new Dictionary<string, ChunkSizeConfig>(StringComparer.OrdinalIgnoreCase)
    {
        // Documents - smaller chunks for better delta efficiency (frequently edited)
        [".docx"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),      // 32KB-256KB, ~128KB avg
        [".xlsx"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".pptx"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".doc"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".xls"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".ppt"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".odt"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".ods"] = new(32 * KB, 256 * KB, MaskFromBits(MediumChunkMaskBits)),
        [".pdf"] = new(64 * KB, 512 * KB, DefaultChunkMask),

        // Text/Code - small chunks (frequently edited, small changes)
        [".txt"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),       // 16KB-128KB, ~64KB avg
        [".md"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".cs"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".js"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".ts"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".py"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".java"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".cpp"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".h"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".json"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".xml"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".yaml"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".yml"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".html"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),
        [".css"] = new(16 * KB, 128 * KB, MaskFromBits(SmallChunkMaskBits)),

        // Media - very large chunks (write-once, never edited)
        // 16MB max significantly reduces Azure operations for large media libraries
        [".mp4"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),   // 1MB-16MB, ~4MB avg
        [".mkv"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".avi"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".mov"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".wmv"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".webm"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".flv"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".m4v"] = new(1 * MB, 64 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".mp3"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),     // 512KB-8MB, ~2MB avg
        [".flac"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".wav"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".aac"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".ogg"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".wma"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".m4a"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),

        // Images - large chunks (write-once, rarely edited)
        [".jpg"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),     // 256KB-4MB, ~1MB avg
        [".jpeg"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".png"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".gif"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".bmp"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".tiff"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".tif"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".webp"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".heic"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".heif"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".raw"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),     // RAW photos are larger
        [".cr2"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".nef"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),
        [".arw"] = new(512 * KB, 8 * MB, MaskFromBits(XLargeChunkMaskBits)),

        // Archives - medium-large chunks (already compressed, write-once)
        [".zip"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),     // 256KB-4MB, ~1MB avg
        [".7z"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".rar"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".tar"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".gz"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),
        [".xz"] = new(256 * KB, 4 * MB, MaskFromBits(LargeChunkMaskBits)),

        // Disk images / VMs - very large chunks (write-once typically)
        [".iso"] = new(2 * MB, 64 * MB, MaskFromBits(HugeChunkMaskBits)), // 2MB-32MB, ~8MB avg
        [".img"] = new(2 * MB, 64 * MB, MaskFromBits(HugeChunkMaskBits)),
        [".vhd"] = new(1 * MB, 16 * MB, MaskFromBits(XXLargeChunkMaskBits)),     // VMs may have sparse changes
        [".vhdx"] = new(1 * MB, 16 * MB, MaskFromBits(XXLargeChunkMaskBits)),
        [".vmdk"] = new(1 * MB, 16 * MB, MaskFromBits(XXLargeChunkMaskBits)),

        // Databases - medium chunks (may have localized changes)
        [".db"] = new(128 * KB, 1 * MB, MaskFromBits(DefaultMaskBits)),          // 128KB-1MB, ~256KB avg
        [".sqlite"] = new(128 * KB, 1 * MB, MaskFromBits(DefaultMaskBits)),
        [".mdf"] = new(256 * KB, 2 * MB, MaskFromBits(DefaultMaskBits)),
        [".ldf"] = new(256 * KB, 2 * MB, MaskFromBits(DefaultMaskBits)),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Large file threshold - files larger than this use optimized large chunks
    private const long LargeFileThreshold = 500L * MB; // 500 MB

    // Large file chunking config: 16 MB min, 128 MB max, ~64 MB average
    private static readonly ChunkSizeConfig LargeFileConfig = new(
        16 * MB,                                     // 16 MB minimum
        128 * MB,                                    // 128 MB maximum  
        MaskFromBits(GiantChunkMaskBits));           // 26 bits = ~64 MB average

    /// <summary>
    /// Gets the chunk configuration for a file based on its extension and size.
    /// Files larger than 500 MB use optimized 64 MB average chunks regardless of type.
    /// </summary>
    private static ChunkSizeConfig GetChunkConfig(string filePath, long fileSize)
    {
        // For large files (>500 MB), use large chunk config for efficiency
        // This significantly reduces Azure API operations and memory usage
        if (fileSize > LargeFileThreshold)
        {
            return LargeFileConfig;
        }

        var ext = Path.GetExtension(filePath);
        return ChunkConfigByExtension.GetValueOrDefault(ext,
            new ChunkSizeConfig(DefaultMinChunkSize, DefaultMaxChunkSize, DefaultChunkMask));
    }

    /// <summary>
    /// Finalizes an IncrementalHash and returns the hex string.
    /// </summary>
    private static string FinalizeFileHash(IncrementalHash hasher)
    {
        Span<byte> hash = stackalloc byte[32];
        hasher.TryGetHashAndReset(hash, out _);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a chunk for content-addressable storage.
    /// SHA-256 provides collision resistance for deduplication safety.
    /// </summary>
    private static string ComputeChunkHash(ReadOnlySpan<byte> data)
        => HashHelper.ComputeHash(data);

    /// <summary>
    /// Chunks a file and streams only the changed chunks to a bounded channel in a single file open.
    /// Phase 1: Sequential CDC pass — produces chunk metadata + file hash (no data retained).
    /// Phase 2: Filtered seek pass — re-reads only chunks not in existingHashes from the same stream.
    /// The caller provides consumer tasks that read ChunkPayloads from the channel for upload.
    /// Backpressure is handled by the bounded channel — the producer blocks when consumers are busy.
    /// </summary>
    /// <param name="filePath">Path to the file to chunk</param>
    /// <param name="existingHashes">Hashes of chunks already stored in Azure (from previous backup)</param>
    /// <param name="channel">Bounded channel to write changed chunk payloads to</param>
    /// <param name="cdcProgress">Reports CDC phase progress as (bytesProcessed, totalBytes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All chunk metadata and the file-level SHA-256 hash</returns>
    public async Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndStreamChangedAsync(
        string filePath,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(existingHashes);
        ArgumentNullException.ThrowIfNull(channel);

        List<ChunkInfo> chunks = [];
        using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var streamBufferSize = (int)Math.Clamp(new FileInfo(filePath).Length, 4096, 1024 * 1024);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: streamBufferSize, useAsync: true);

        var fileLength = stream.Length;
        var config = GetChunkConfig(filePath, fileLength);

        // Phase 1 & 2 combined for small files: single chunk, read once, write to channel if changed
        if (fileLength <= config.MinChunkSize)
        {
            var dataLength = (int)fileLength;
            // ArrayPool.Rent(0) throws; rent minimum of 1 for empty files
            var data = ArrayPool<byte>.Shared.Rent(Math.Max(dataLength, 1));
            await stream.ReadExactlyAsync(data.AsMemory(0, dataLength), cancellationToken);

            fileHasher.AppendData(data.AsSpan(0, dataLength));

            var info = new ChunkInfo
            {
                Index = 0,
                Offset = 0,
                Length = dataLength,
                Hash = ComputeChunkHash(data.AsSpan(0, dataLength))
            };
            chunks.Add(info);

            cdcProgress?.Report((fileLength, fileLength, 1));

            if (!existingHashes.Contains(info.Hash))
            {
                await channel.WriteAsync(new ChunkPayload(info, data, dataLength), cancellationToken);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(data);
                info.BlobName = $"chunks/{info.Hash}";
            }

            return (chunks, FinalizeFileHash(fileHasher));
        }

        // Phase 1: CDC pass — sequential read, builds chunk list + file hash, no data retained
        var cdcBufferSize = (int)Math.Min((long)config.MaxChunkSize + WindowSize, fileLength);
        var buffer = ArrayPool<byte>.Shared.Rent(cdcBufferSize);
        try
        {
            var chunkStart = 0L;
            var position = 0L;
            var chunkIndex = 0;
            var rollingHash = 0u;
            byte[] window = new byte[WindowSize];
            var windowPos = 0;
            var windowFilled = false;

            while (position < fileLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(buffer.Length, fileLength - chunkStart);
                stream.Position = chunkStart;
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);

                if (bytesRead == 0) break;

                var chunkLength = 0;

                for (var i = 0; i < bytesRead; i++)
                {
                    var b = buffer[i];
                    chunkLength++;

                    if (windowFilled)
                    {
                        var outByte = window[windowPos];
                        rollingHash = (rollingHash - outByte * HashPrimePower) * HashPrime + b;
                    }
                    else
                    {
                        rollingHash = rollingHash * HashPrime + b;
                    }

                    window[windowPos] = b;
                    // Branchless-friendly wrap (~5-10% faster than modulo on x64
                    // because WindowSize=48 is not a power of two, so the JIT cannot
                    // lower `% WindowSize` to an AND). Keeping WindowSize=48 avoids
                    // changing CDC chunk boundaries for existing backups.
                    if (++windowPos == WindowSize)
                    {
                        windowPos = 0;
                        windowFilled = true;
                    }

                    var isChunkBoundary = chunkLength >= config.MinChunkSize &&
                                          ((rollingHash & config.ChunkMask) == 0 || chunkLength >= config.MaxChunkSize);

                    var isEndOfFile = chunkStart + chunkLength >= fileLength;

                    if (isChunkBoundary || isEndOfFile)
                    {
                        var chunkSpan = buffer.AsSpan(0, chunkLength);
                        fileHasher.AppendData(chunkSpan);

                        chunks.Add(new ChunkInfo
                        {
                            Index = chunkIndex++,
                            Offset = chunkStart,
                            Length = chunkLength,
                            Hash = ComputeChunkHash(chunkSpan)
                        });

                        chunkStart += chunkLength;
                        position = chunkStart;
                        chunkLength = 0;
                        rollingHash = 0;
                        windowFilled = false;
                        windowPos = 0;
                        Array.Clear(window);

                        cdcProgress?.Report((chunkStart, fileLength, chunks.Count));

                        break;
                    }
                }

                if (chunkStart >= fileLength) break;
            }

            // Phase 2: Filtered seek pass — re-read only changed chunks from the same stream.
            // Chunk data uses rented ArrayPool buffers to avoid LOH allocations.
            // The upload consumer returns these buffers after encrypting and uploading.
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (existingHashes.Contains(chunk.Hash))
                {
                    chunk.BlobName = $"chunks/{chunk.Hash}";
                    continue;
                }

                stream.Position = chunk.Offset;
                var data = ArrayPool<byte>.Shared.Rent(chunk.Length);
                await stream.ReadExactlyAsync(data.AsMemory(0, chunk.Length), cancellationToken);

                // Verify the re-read data matches the Phase 1 hash.
                // The file could have been modified between CDC (Phase 1) and this seek pass (Phase 2).
                // Without this check, we'd silently upload data that doesn't match the stored hash,
                // causing every future restore to fail chunk hash verification.
                var rereadHash = ComputeChunkHash(data.AsSpan(0, chunk.Length));
                if (!string.Equals(rereadHash, chunk.Hash, StringComparison.Ordinal))
                {
                    ArrayPool<byte>.Shared.Return(data);
                    FileOperationDiagnostics.RecordAmbient(
                        $"[FILE MODIFIED] Phase 2 re-read hash mismatch for chunk {chunk.Index}: " +
                        $"phase1={chunk.Hash[..12]}..., phase2={rereadHash[..12]}..., " +
                        $"offset={chunk.Offset}, length={chunk.Length}");
                    throw new IOException(
                        $"File was modified during backup (chunk {chunk.Index} hash changed between CDC and upload). " +
                        $"The file will be retried on the next backup cycle.");
                }

                await channel.WriteAsync(new ChunkPayload(chunk, data, chunk.Length), cancellationToken);
            }

            return (chunks, FinalizeFileHash(fileHasher));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a specific chunk from a file.
    /// </summary>
    public async Task<byte[]> ReadChunkAsync(string filePath, ChunkInfo chunk, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(chunk);

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        stream.Position = chunk.Offset;
        byte[] data = new byte[chunk.Length];
        await stream.ReadExactlyAsync(data, cancellationToken);

        return data;
    }

}
