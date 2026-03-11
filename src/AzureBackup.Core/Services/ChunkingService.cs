using System.IO.Hashing;
using System.Security.Cryptography;
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
    private const int MB = 1024 * KB;

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

    // Precomputed: HashPrime^(WindowSize-1)
    private static readonly uint HashPrimePower = ComputePower(HashPrime, WindowSize - 1);

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
    private static readonly Dictionary<string, ChunkSizeConfig> ChunkConfigByExtension = new(StringComparer.OrdinalIgnoreCase)
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
    };

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
    /// Splits a file into content-defined chunks.
    /// Returns chunk information including hash and boundaries.
    /// Chunk sizes are adaptive based on file type and size.
    /// Files larger than 500 MB use 64 MB average chunks for efficiency.
    /// </summary>
    public async Task<List<ChunkInfo>> ChunkFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        List<ChunkInfo> chunks = new();
        
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 
            bufferSize: 1024 * 1024, useAsync: true);
        
        var fileLength = stream.Length;
        var config = GetChunkConfig(filePath, fileLength);
        
        // For small files, treat as single chunk
        if (fileLength <= config.MinChunkSize)
        {
            byte[] data = new byte[fileLength];
            await stream.ReadExactlyAsync(data, cancellationToken);
            
            chunks.Add(new ChunkInfo
            {
                Index = 0,
                Offset = 0,
                Length = (int)fileLength,
                Hash = ComputeChunkHash(data)
            });
            
            return chunks;
        }

        // Content-defined chunking for larger files
        // Buffer size adapts to max chunk size for the file type
        byte[] buffer = new byte[config.MaxChunkSize + WindowSize];
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
                
                // Update rolling hash
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
                windowPos = (windowPos + 1) % WindowSize;
                if (windowPos == 0) windowFilled = true;

                // Check for chunk boundary using adaptive config
                var isChunkBoundary = chunkLength >= config.MinChunkSize && 
                                      ((rollingHash & config.ChunkMask) == 0 || chunkLength >= config.MaxChunkSize);
                
                var isEndOfFile = chunkStart + chunkLength >= fileLength;
                
                if (isChunkBoundary || isEndOfFile)
                {
                    var chunkData = buffer.AsSpan(0, chunkLength).ToArray();
                    
                    chunks.Add(new ChunkInfo
                    {
                        Index = chunkIndex++,
                        Offset = chunkStart,
                        Length = chunkLength,
                        Hash = ComputeChunkHash(chunkData)
                    });

                    chunkStart += chunkLength;
                    position = chunkStart;
                    chunkLength = 0;
                    rollingHash = 0;
                    windowFilled = false;
                    windowPos = 0;
                    Array.Clear(window);
                    
                    break; // Move to next buffer read
                }
            }
            
            // Handle case where we read the entire remaining file without hitting a boundary
            if (chunkStart >= fileLength) break;
        }

        return chunks;
    }

    /// <summary>
    /// Computes the SHA-256 hash of a chunk for content-addressable storage.
    /// SHA-256 provides collision resistance for deduplication safety.
    /// </summary>
    private static string ComputeChunkHash(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes SHA-256 hash of file for integrity verification.
    /// </summary>
    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Compares two sets of chunks and returns which chunks need to be uploaded.
    /// </summary>
    public List<ChunkInfo> GetChangedChunks(List<ChunkInfo> existingChunks, List<ChunkInfo> newChunks)
    {
        ArgumentNullException.ThrowIfNull(existingChunks);
        ArgumentNullException.ThrowIfNull(newChunks);
        
        var existingHashes = existingChunks.Select(c => c.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return newChunks.Where(c => !existingHashes.Contains(c.Hash)).ToList();
    }

    /// <summary>
    /// Reads a specific chunk from a file.
    /// </summary>
    public async Task<byte[]> ReadChunkAsync(string filePath, ChunkInfo chunk, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(chunk);
        
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: Math.Max(4096, chunk.Length), useAsync: true);
        
        stream.Position = chunk.Offset;
        byte[] data = new byte[chunk.Length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        
        return data;
    }

    private static uint ComputePower(uint baseVal, int exp)
    {
        uint result = 1;
        while (exp > 0)
        {
            if ((exp & 1) == 1)
                result *= baseVal;
            baseVal *= baseVal;
            exp >>= 1;
        }
        return result;
    }
}
