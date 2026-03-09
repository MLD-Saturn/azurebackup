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
    // Default chunking parameters
    private const int DefaultMinChunkSize = 64 * 1024;       // 64 KB minimum chunk
    private const int DefaultMaxChunkSize = 1024 * 1024;     // 1 MB maximum chunk
    private const uint DefaultChunkMask = 0x0003FFFF;        // 18 bits = ~256KB average

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
    /// File extensions mapped to chunking configurations.
    /// Mask bits determine average chunk size: 2^bits bytes average.
    /// </summary>
    private static readonly Dictionary<string, ChunkSizeConfig> ChunkConfigByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents - smaller chunks for better delta efficiency (frequently edited)
        [".docx"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),      // 32KB-256KB, ~128KB avg
        [".xlsx"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".pptx"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".doc"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".xls"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".ppt"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".odt"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".ods"] = new(32 * 1024, 256 * 1024, 0x0001FFFF),
        [".pdf"] = new(64 * 1024, 512 * 1024, 0x0003FFFF),

        // Text/Code - small chunks (frequently edited, small changes)
        [".txt"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),       // 16KB-128KB, ~64KB avg
        [".md"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".cs"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".js"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".ts"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".py"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".java"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".cpp"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".h"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".json"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".xml"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".yaml"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".yml"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".html"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),
        [".css"] = new(16 * 1024, 128 * 1024, 0x0000FFFF),

        // Media - very large chunks (write-once, never edited)
        // 16MB max significantly reduces Azure operations for large media libraries
        [".mp4"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),   // 1MB-16MB, ~4MB avg
        [".mkv"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".avi"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".mov"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".wmv"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".webm"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".flv"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".m4v"] = new(1024 * 1024, 64 * 1024 * 1024, 0x003FFFFF),
        [".mp3"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),     // 512KB-8MB, ~2MB avg
        [".flac"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".wav"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".aac"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".ogg"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".wma"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".m4a"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),

        // Images - large chunks (write-once, rarely edited)
        [".jpg"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),     // 256KB-4MB, ~1MB avg
        [".jpeg"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".png"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".gif"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".bmp"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".tiff"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".tif"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".webp"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".heic"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".heif"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".raw"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),     // RAW photos are larger
        [".cr2"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".nef"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),
        [".arw"] = new(512 * 1024, 8 * 1024 * 1024, 0x001FFFFF),

        // Archives - medium-large chunks (already compressed, write-once)
        [".zip"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),     // 256KB-4MB, ~1MB avg
        [".7z"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".rar"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".tar"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".gz"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),
        [".xz"] = new(256 * 1024, 4 * 1024 * 1024, 0x000FFFFF),

        // Disk images / VMs - very large chunks (write-once typically)
        [".iso"] = new(2 * 1024 * 1024, 64 * 1024 * 1024, 0x007FFFFF), // 2MB-32MB, ~8MB avg
        [".img"] = new(2 * 1024 * 1024, 64 * 1024 * 1024, 0x007FFFFF),
        [".vhd"] = new(1024 * 1024, 16 * 1024 * 1024, 0x003FFFFF),     // VMs may have sparse changes
        [".vhdx"] = new(1024 * 1024, 16 * 1024 * 1024, 0x003FFFFF),
        [".vmdk"] = new(1024 * 1024, 16 * 1024 * 1024, 0x003FFFFF),

        // Databases - medium chunks (may have localized changes)
        [".db"] = new(128 * 1024, 1024 * 1024, 0x0003FFFF),          // 128KB-1MB, ~256KB avg
        [".sqlite"] = new(128 * 1024, 1024 * 1024, 0x0003FFFF),
        [".mdf"] = new(256 * 1024, 2 * 1024 * 1024, 0x0007FFFF),
        [".ldf"] = new(256 * 1024, 2 * 1024 * 1024, 0x0007FFFF),
    };

    // Large file threshold - files larger than this use optimized large chunks
    private const long LargeFileThreshold = 500L * 1024 * 1024; // 500 MB
    
    // Large file chunking config: 16 MB min, 128 MB max, ~64 MB average
    // Mask 0x03FFFFFF = 26 bits = 64 MB average chunk size
    private static readonly ChunkSizeConfig LargeFileConfig = new(
        16 * 1024 * 1024,      // 16 MB minimum
        128 * 1024 * 1024,     // 128 MB maximum  
        0x03FFFFFF);           // 26 bits = ~64 MB average

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
