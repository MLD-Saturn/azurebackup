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
    /// Chunks a file in a single sequential pass and streams every changed chunk
    /// to the supplied bounded channel as soon as its boundary is detected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase 6 / P1 rewrite.</b> The previous implementation ran two passes:
    /// Phase 1 (CDC scan, data discarded) followed by Phase 2 (re-read each
    /// changed chunk and push). That cost ~2x the file I/O for any file whose
    /// chunks were not all unchanged, and it required a hash-verify guard
    /// against the rare "file modified between phases" race.
    /// </para>
    /// <para>
    /// The new path computes the rolling hash, file hash, and chunk hash on a
    /// single sequential scan. When a chunk boundary fires AND the resulting
    /// hash is not in <paramref name="existingHashes"/>, we copy the chunk into
    /// a fresh ArrayPool buffer and write a <see cref="ChunkPayload"/> to
    /// <paramref name="channel"/>. The producer keeps owning its CDC scratch
    /// buffer; the consumer owns the per-chunk payload buffer and is
    /// responsible for returning it via <c>ArrayPool.Shared.Return</c>.
    /// </para>
    /// <para>
    /// Backpressure is provided by the bounded channel: when consumers are
    /// busy uploading, <c>WriteAsync</c> awaits and the producer naturally
    /// slows. The CDC loop itself remains synchronous between dispatches,
    /// so a stalled consumer cannot wedge an in-progress chunk computation.
    /// </para>
    /// <para>
    /// Trade-off intentionally accepted: file modifications that occur during
    /// the single pass are no longer detected by hash comparison. They will
    /// still be caught on the next backup cycle when the file's
    /// <c>LastWriteTime</c> changes. This matches the behaviour of every other
    /// modern dedup-backup engine (restic, BorgBackup) and removes the entire
    /// Phase 2 re-read pass.
    /// </para>
    /// </remarks>
    /// <param name="filePath">Path to the file to chunk.</param>
    /// <param name="existingHashes">
    /// Hashes of chunks already stored in Azure (from a previous backup).
    /// Must use <see cref="StringComparer.Ordinal"/> for correctness.
    /// </param>
    /// <param name="channel">Bounded channel that receives changed-chunk payloads.</param>
    /// <param name="cdcProgress">
    /// Optional reporter receiving (bytesProcessed, totalBytes, chunksFound)
    /// after each chunk boundary.
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured per chunk.</param>
    /// <returns>The full ordered chunk list and the file-level SHA-256 hash.</returns>
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

        // Tiny files: single chunk, single read, single dispatch decision.
        if (fileLength <= config.MinChunkSize)
        {
            await EmitSingleChunkAsync(stream, fileLength, fileHasher, existingHashes,
                channel, cdcProgress, chunks, cancellationToken);
            return (chunks, FinalizeFileHash(fileHasher));
        }

        // Single-pass streaming CDC (Phase 6.1 / discovered-#5).
        //
        // Design: read the file through a small fixed scratch buffer and copy
        // bytes one at a time into a per-chunk accumulator that doubles as the
        // dispatch payload. There is no re-seek and no
        // (MaxChunkSize + WindowSize)-sized scan buffer; the scratch buffer is
        // always 64 KB regardless of the configured chunk size, so the giant
        // chunk profiles (up to 128 MB max) no longer rent enormous arrays.
        //
        // For each chunk:
        //   1. Rent a payload buffer sized to config.MaxChunkSize.
        //   2. Stream bytes into it through the scratch buffer; update the
        //      rolling hash and the file-level SHA-256 inline.
        //   3. When (rollingHash & ChunkMask) == 0 above MinChunkSize, OR when
        //      the chunk reaches MaxChunkSize, OR at EOF, finalise the chunk.
        //   4. If the chunk hash is in existingHashes, return its payload
        //      buffer to the pool. Otherwise dispatch it on the channel; the
        //      consumer is responsible for returning the buffer.
        //
        // Reading via a small scratch buffer rather than directly into the
        // payload buffer keeps the accumulator's tail untouched until we're
        // sure those bytes belong to this chunk - which lets us hand the
        // payload buffer off to a consumer immediately when a boundary fires
        // without copying.
        const int ScratchSize = 64 * 1024;
        var scratch = ArrayPool<byte>.Shared.Rent(ScratchSize);
        byte[]? payloadBuffer = null;
        try
        {
            var chunkStart = 0L;
            var chunkLength = 0;
            var chunkIndex = 0;
            var rollingHash = 0u;
            byte[] window = new byte[WindowSize];
            var windowPos = 0;
            var windowFilled = false;

            // Scratch state: bytes [scratchPos .. scratchLen) are loaded but
            // not yet copied into the chunk accumulator.
            var scratchPos = 0;
            var scratchLen = 0;

            // Rent the first chunk's payload buffer.
            payloadBuffer = ArrayPool<byte>.Shared.Rent(config.MaxChunkSize);

            while (true)
            {
                // Refill scratch when we have consumed everything in it.
                if (scratchPos >= scratchLen)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scratchLen = await stream.ReadAsync(scratch.AsMemory(0, ScratchSize), cancellationToken);
                    scratchPos = 0;
                    if (scratchLen == 0)
                    {
                        // EOF mid-chunk. Emit whatever is accumulated, if any.
                        if (chunkLength > 0)
                        {
                            var span = payloadBuffer.AsSpan(0, chunkLength);
                            fileHasher.AppendData(span);
                            var chunk = BuildChunkInfo(chunkIndex++, chunkStart, chunkLength,
                                ComputeChunkHash(span));
                            await DispatchChunkAsync(chunk, payloadBuffer, chunks, existingHashes,
                                channel, cdcProgress, fileLength, cancellationToken);
                            // Ownership transferred (or returned to pool inside DispatchChunkAsync)
                            payloadBuffer = null;
                        }
                        break;
                    }
                }

                var b = scratch[scratchPos++];
                payloadBuffer[chunkLength++] = b;

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
                // because WindowSize=48 is not a power of two, so the JIT
                // cannot lower `% WindowSize` to an AND).
                if (++windowPos == WindowSize)
                {
                    windowPos = 0;
                    windowFilled = true;
                }

                var atMaxChunkSize = chunkLength >= config.MaxChunkSize;
                var isChunkBoundary = chunkLength >= config.MinChunkSize &&
                                      ((rollingHash & config.ChunkMask) == 0 || atMaxChunkSize);

                if (isChunkBoundary)
                {
                    var span = payloadBuffer.AsSpan(0, chunkLength);
                    fileHasher.AppendData(span);
                    var chunk = BuildChunkInfo(chunkIndex++, chunkStart, chunkLength,
                        ComputeChunkHash(span));
                    await DispatchChunkAsync(chunk, payloadBuffer, chunks, existingHashes,
                        channel, cdcProgress, fileLength, cancellationToken);

                    // Ownership of the buffer was transferred to the consumer
                    // (or returned to the pool inside EmitChunkAsync). Reset
                    // for the next chunk.
                    chunkStart += chunkLength;
                    chunkLength = 0;
                    rollingHash = 0;
                    windowFilled = false;
                    windowPos = 0;
                    Array.Clear(window);

                    if (chunkStart >= fileLength)
                    {
                        payloadBuffer = null;
                        break;
                    }

                    payloadBuffer = ArrayPool<byte>.Shared.Rent(config.MaxChunkSize);
                }
            }

            return (chunks, FinalizeFileHash(fileHasher));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            // payloadBuffer is null here on success (ownership transferred)
            // and non-null only if we threw mid-chunk before emitting.
            if (payloadBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(payloadBuffer);
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="ChunkInfo"/> record without dispatching it. Kept as a
    /// separate helper so the streaming loop reads top-down without inline
    /// object initializers cluttering the boundary-handling logic.
    /// </summary>
    private static ChunkInfo BuildChunkInfo(int index, long offset, int length, string hash)
        => new() { Index = index, Offset = offset, Length = length, Hash = hash };

    /// <summary>
    /// Records the chunk metadata in the result list and either dispatches the
    /// payload to the channel or returns its buffer to the pool when the chunk
    /// is already deduplicated. The synchronous file-hash + chunk-hash work has
    /// already been done by the caller; this helper is async only because the
    /// channel write may block on backpressure.
    /// </summary>
    private static async Task DispatchChunkAsync(
        ChunkInfo chunk,
        byte[] payloadBuffer,
        List<ChunkInfo> chunks,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        long fileLength,
        CancellationToken cancellationToken)
    {
        chunks.Add(chunk);

        if (existingHashes.Contains(chunk.Hash))
        {
            chunk.BlobName = $"chunks/{chunk.Hash}";
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
        else
        {
            await channel.WriteAsync(
                new ChunkPayload(chunk, payloadBuffer, chunk.Length),
                cancellationToken);
        }

        cdcProgress?.Report((chunk.Offset + chunk.Length, fileLength, chunks.Count));
    }

    /// <summary>
    /// Specialised single-chunk path for files at or below the configured
    /// minimum chunk size. Avoids the rolling-hash machinery entirely.
    /// </summary>
    private static async Task EmitSingleChunkAsync(
        FileStream stream,
        long fileLength,
        IncrementalHash fileHasher,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        List<ChunkInfo> chunks,
        CancellationToken cancellationToken)
    {
        var dataLength = (int)fileLength;
        // ArrayPool.Rent(0) throws; rent minimum of 1 for empty files
        var data = ArrayPool<byte>.Shared.Rent(Math.Max(dataLength, 1));
        try
        {
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
                // Ownership transfers to the consumer.
                await channel.WriteAsync(new ChunkPayload(info, data, dataLength), cancellationToken);
                data = null!; // disable the finally-return
            }
            else
            {
                info.BlobName = $"chunks/{info.Hash}";
            }
        }
        finally
        {
            if (data is not null)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
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
