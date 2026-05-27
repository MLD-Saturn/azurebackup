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
    private const long LargeFileThreshold = 500L * MB;

    // Large file chunking config: 16 MB min, 128 MB max, ~64 MB average
    private static readonly ChunkSizeConfig LargeFileConfig = new(
        16 * MB,                                     // 16 MB minimum
        128 * MB,                                    // 128 MB maximum  
        MaskFromBits(GiantChunkMaskBits));           // 26 bits = ~64 MB average

    /// <summary>
    /// B33: chunks whose configured <c>MaxChunkSize</c> is at or above this
    /// threshold bypass <see cref="ArrayPool{T}.Shared"/> and use an
    /// exact-sized <c>byte[]</c> allocation instead.
    /// <para>
    /// Rationale: <c>ArrayPool&lt;byte&gt;.Shared</c> rounds rented sizes up
    /// to the next power-of-two tier (a 64 MB rent returns a 64 MB array,
    /// a 65 MB rent returns a 128 MB array) and parks released arrays in
    /// per-core buckets that the runtime never gives back to the OS. Both
    /// behaviours make the actual heap residency invisible to
    /// <see cref="MemoryBudget"/>: the budget would charge
    /// <c>chunkLength × 2</c> while the pool was holding tens of GB of
    /// large arrays parked across cores. Exact <c>byte[]</c> allocations
    /// are LOH-residents that the GC reclaims on the next gen-2
    /// collection -- not free, but at least visible and bounded.
    /// </para>
    /// <para>
    /// Threshold chosen so the default extension-based config families
    /// (text/code 128 KB, documents 256 KB, images 4 MB, audio 8 MB)
    /// still benefit from the pool's per-core caching, while the
    /// large-file (16-128 MB) and video (16-64 MB) paths -- which
    /// dominate the residency budget on a multi-TB backup -- skip the
    /// pool entirely.
    /// </para>
    /// </summary>
    internal const int PoolSkipThresholdBytes = 16 * MB;

    /// <summary>
    /// B30: rents (or allocates) a chunk-payload buffer and charges the
    /// shared <see cref="MemoryBudget"/> for the actual allocated size
    /// PLUS the downstream encrypt-side rented buffer that the consumer
    /// will allocate when it picks the chunk off the channel (B38).
    /// Returns the buffer, the charged byte count, and a flag indicating
    /// whether the consumer should return the buffer to the pool.
    /// <para>
    /// The buffer is sized to <paramref name="payloadSize"/>, which the
    /// caller must set to the chunk's <c>MaxChunkSize</c> (so the producer
    /// can fill up to that size before reaching a boundary). For chunks
    /// whose configured max meets or exceeds
    /// <see cref="PoolSkipThresholdBytes"/> the allocation is an exact
    /// <c>byte[]</c>; otherwise it is an <see cref="ArrayPool{T}.Shared"/>
    /// rental.
    /// </para>
    /// <para>
    /// B38: the encrypt-side buffer (chunk + 37 bytes overhead, rounded up
    /// to <see cref="ArrayPool{T}.Shared"/>'s tier ceiling) is charged
    /// HERE, on the producer side, in the same atomic Acquire as the
    /// payload buffer. This avoids a producer-vs-consumer circular wait
    /// on the budget: an alternative design where the consumer
    /// re-acquires for the encrypt buffer can deadlock when the producer
    /// charge fills the budget before the consumer's Acquire can run, and
    /// the producer charge can only release after the consumer finishes
    /// its upload -- which it cannot start because its Acquire is blocked
    /// on the producer charge. Charging both stages on the producer side
    /// in one atomic operation makes the budget a strict throttle on the
    /// producer alone; the consumer never Acquires, only Releases.
    /// </para>
    /// <para>
    /// Charging happens BEFORE the buffer is allocated so the budget can
    /// throttle the producer. With B30+B38 in place the producer is the
    /// budget-binding stage; the channel-buffered ChunkPayloads, the
    /// consumer-side encrypt buffer, and any SDK staging downstream all
    /// live within the headroom the producer has already reserved.
    /// </para>
    /// </summary>
    private static async Task<(byte[] Buffer, long ChargedBytes, bool ReturnToPool, ChunkBufferPool? AssignedPool)> AcquireChunkBufferAsync(
        int payloadSize,
        MemoryBudget? budget,
        ChunkBufferPool? largeChunkPool,
        ChunkBufferPool? smallChunkPool,
        CancellationToken cancellationToken)
    {
        // Decide pool vs exact-allocation BEFORE charging so the charged
        // amount matches the actual residency that will be created.
        var skipPool = payloadSize >= PoolSkipThresholdBytes;
        long payloadCharge;
        byte[] buffer;
        bool returnToPool;
        ChunkBufferPool? assignedPool = null;

        if (skipPool)
        {
            // Exact allocation: charge exactly the requested size.
            payloadCharge = payloadSize;
        }
        else
        {
            // B69: when a small-chunk ChunkBufferPool is wired the rent
            // rounds up to that pool's next bucket size, NOT the
            // ArrayPool tier size, so the charge has to match the bucket
            // the pool will actually hand back.
            // Pre-B69 / fallback path: ArrayPool. Charge the rounded-up
            // tier size that the pool will actually hand back, not the
            // request size. This is the difference that closes the
            // largest pre-B30 accounting gap. We compute the tier ceiling
            // defensively without duplicating ArrayPool's internal sizing
            // -- worst case the tier rounds up to next power-of-two, so
            // we charge that.
            payloadCharge = smallChunkPool != null
                ? BucketCeiling(payloadSize, smallChunkPool)
                : NextPowerOfTwoOrSelf(payloadSize);
        }

        // B38: add the encrypt-side rented buffer to the charge. The
        // encrypt-side allocation is `payloadSize + EncryptionOverhead`
        // bytes rented from ArrayPool<byte>.Shared and is alive from the
        // moment the consumer picks up the payload until the upload
        // completes. We charge its tier ceiling here so the budget
        // covers the chunk's full pipeline residency in one Acquire.
        //
        // Note: when payloadSize is itself a power of two (e.g. exactly
        // 128 MB on the LargeFileConfig path), encrypt rounds up to the
        // NEXT tier (256 MB) because of the 37-byte overhead. That is
        // the correct charge for that case -- ArrayPool will indeed
        // hand back the next-larger tier.
        //
        // B74 (W5 Phase 4 Commit 3, Fix B): when B73 routes the
        // encrypted-buffer rent through the small ChunkBufferPool, the
        // pool's bucket geometry [64K, 256K, 1M, 4M, 16M] has 4x gaps
        // that NextPowerOfTwoOrSelf (which matches ArrayPool's
        // power-of-two tier shape) under-charges by up to 2x. Concrete
        // case: a 4 MB plaintext chunk encrypts to 4 MB + 37 bytes,
        // which the small pool rents at 16 MB but the pre-B74 charge
        // computed as 8 MB -- an 8 MB per-chunk under-charge that
        // could undercount multi-GB at 96-way in-flight worst case.
        // The corrected branch picks the matching pool's BucketCeiling
        // when the encrypt rent will route through a ChunkBufferPool;
        // when the encrypted size crosses PoolSkipThresholdBytes B74's
        // C2 fix routes the rent back to ArrayPool<byte>.Shared, so
        // NextPowerOfTwoOrSelf is the correct charge for that branch.
        var encryptedPayloadLen = payloadSize + EncryptionService.EncryptionOverhead;
        var encryptCharge = (long)(
            encryptedPayloadLen < PoolSkipThresholdBytes && smallChunkPool != null
                ? BucketCeiling(encryptedPayloadLen, smallChunkPool)
                : NextPowerOfTwoOrSelf(encryptedPayloadLen));

        // B55 (W3 Phase D): fold the Azure SDK's per-upload staging
        // residency into the producer-side charge. The SDK retains
        // `MaximumConcurrency × MaximumTransferSize` bytes per in-flight
        // upload (see AzureBlobService.ComputeUploadTransferOptions);
        // pre-B55 those bytes were entirely invisible to the budget and
        // could push process working set well above MemoryLimitMB on a
        // 16-way file × 6-way chunk pipeline. Since B53 the staging
        // shape is chunk-size-gated, so the per-chunk staging estimate
        // is exact rather than the worst-case 64 MB constant.
        //
        // The estimate is a conservative upper bound -- the SDK does
        // not necessarily hold the full N×M block at every instant --
        // but the budget needs an upper bound to do its job. Charging
        // the upper bound at acquire time is exactly the same pattern
        // B30/B38 already use for the chunk and encrypt buffers.
        var stagingCharge = AzureBlobService.EstimateUploadStagingBytes(encryptedPayloadLen);

        var chargedBytes = payloadCharge + encryptCharge + stagingCharge;
        if (budget != null)
            await budget.AcquireAsync(chargedBytes, cancellationToken);

        if (skipPool)
        {
            // B37: when a large-chunk ChunkBufferPool is supplied, the
            // skip-pool allocation goes through the bounded LOH
            // recycler instead of `new byte[]`. The recycler keeps
            // cached buffers ALIVE forever so the GC never has to
            // reclaim a 128 MB LOH-resident array between gen-2
            // collections, which was the dominant residency leak the
            // B36 memory log surfaced after B30/B33/B34 landed. The
            // recycler is bounded (per-bucket cap) so total residency
            // is strictly capped; overflow above the cap allocates
            // fresh, falling back to the pre-B37 GC-managed path. The
            // consumer sees a buffer whose `Length` matches the
            // bucket size (>= payloadSize), but the chunk loop only
            // ever writes up to `chunkLength` bytes and slices to
            // that length when handing off, so the bucket-size
            // overshoot is invisible downstream.
            if (largeChunkPool != null)
            {
                var (poolBuffer, _) = largeChunkPool.Rent(payloadSize);
                buffer = poolBuffer;
                returnToPool = false; // not the shared ArrayPool
                assignedPool = largeChunkPool;
            }
            else
            {
                buffer = new byte[payloadSize];
                returnToPool = false;
            }
        }
        else if (smallChunkPool != null)
        {
            // B69: small-chunk path with an owned ChunkBufferPool
            // wired. Replaces ArrayPool<byte>.Shared so the per-core
            // tier caches that previously leaked residency outside the
            // budget become operation-scoped bucket bags that share the
            // budget's lifetime. The pool returns a buffer whose
            // `Length` matches the bucket ceiling; the producer loop
            // already slices to `chunkLength` when handing off, so the
            // bucket-size overshoot is invisible downstream.
            var (poolBuffer, _) = smallChunkPool.Rent(payloadSize);
            buffer = poolBuffer;
            returnToPool = false; // not the shared ArrayPool
            assignedPool = smallChunkPool;
        }
        else
        {
            buffer = ArrayPool<byte>.Shared.Rent(payloadSize);
            returnToPool = true;
        }

        return (buffer, chargedBytes, returnToPool, assignedPool);
    }

    /// <summary>
    /// Releases a chunk-payload buffer that <see cref="AcquireChunkBufferAsync"/>
    /// produced. Matches the rent path (ChunkBufferPool vs ArrayPool
    /// vs raw GC byte[]) so the underlying pool's invariants are
    /// preserved.
    /// </summary>
    private static void ReleaseChunkBuffer(byte[] buffer, bool returnToPool, ChunkBufferPool? assignedPool, long chargedBytes, MemoryBudget? budget)
    {
        if (assignedPool != null)
        {
            assignedPool.Return(buffer);
        }
        else if (returnToPool)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        // Else: raw new byte[] -- let the GC reclaim it. Nothing to do here.

        if (budget != null && chargedBytes > 0)
            budget.Release(chargedBytes);
    }

    /// <summary>
    /// B69: smallest bucket size in <paramref name="pool"/>'s configured
    /// geometry whose capacity is &gt;= <paramref name="value"/>, or
    /// <paramref name="value"/> when the request falls outside the
    /// pool's bucket range. Used so the producer-side budget charge
    /// matches the bucket-sized buffer the pool will actually hand
    /// back; without this the charge would under-account for the
    /// bucket overshoot the pool's <c>Rent</c> performs.
    /// </summary>
    /// <remarks>
    /// B74 (W5 Phase 4 Commit 3): promoted from private to internal
    /// so the charge-vs-rent invariant for the small-pool encrypt-buffer
    /// path can be pinned directly by a focused unit test. The method
    /// itself is unchanged; the visibility bump is purely for testability.
    /// </remarks>
    internal static long BucketCeiling(int value, ChunkBufferPool pool)
    {
        foreach (var size in pool.BucketSizes)
        {
            if (value <= size) return size;
        }
        return value;
    }

    /// <summary>
    /// Smallest power of two that is &gt;= <paramref name="value"/>, or
    /// <paramref name="value"/> itself when it is already a power of two.
    /// Used to estimate <see cref="ArrayPool{T}.Shared"/>'s tier-ceiling
    /// rental size for budget accounting; the pool's actual buckets are
    /// power-of-two-spaced so this approximation is exact for the common
    /// case and conservatively over-charges for the few non-power-of-two
    /// edge sizes the configured chunk maxes happen to land on.
    /// </summary>
    /// <remarks>
    /// B74 (W5 Phase 4 Commit 3): promoted from private to internal
    /// alongside <see cref="BucketCeiling"/> for the same reason -- the
    /// B74 contract test pins the bucket-vs-pow2 invariant directly
    /// rather than going through the producer hot path.
    /// </remarks>
    internal static int NextPowerOfTwoOrSelf(int value)
    {
        if (value <= 1) return 1;
        if ((value & (value - 1)) == 0) return value;
        var n = value - 1;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }

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
    public Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndStreamChangedAsync(
        string filePath,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        CancellationToken cancellationToken = default)
        => ChunkAndStreamChangedAsync(filePath, existingHashes, channel, cdcProgress,
            memoryBudget: null, largeChunkPool: null, cancellationToken);

    /// <summary>
    /// B30 overload: same as <see cref="ChunkAndStreamChangedAsync(string, HashSet{string}, ChannelWriter{ChunkPayload}, IProgress{(long, long, int)}?, CancellationToken)"/>
    /// but charges every payload buffer to <paramref name="memoryBudget"/>
    /// at allocation time. Pass <c>null</c> for the legacy unaccounted
    /// behaviour (used by the CDC benchmarks that never run with a budget).
    /// </summary>
    public Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndStreamChangedAsync(
        string filePath,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        MemoryBudget? memoryBudget,
        CancellationToken cancellationToken = default)
        => ChunkAndStreamChangedAsync(filePath, existingHashes, channel, cdcProgress,
            memoryBudget, largeChunkPool: null, smallChunkPool: null, cancellationToken);

    /// <summary>
    /// B37 overload: as above, plus an optional
    /// <see cref="ChunkBufferPool"/> that the producer rents from for
    /// chunks at or above <see cref="PoolSkipThresholdBytes"/>. When
    /// supplied, chunks large enough to skip
    /// <see cref="ArrayPool{T}.Shared"/> instead flow through the
    /// bounded LOH recycler -- buffers stay alive forever (no gen-2
    /// retention pressure) but the per-bucket cap strictly bounds
    /// total residency. Pass <c>null</c> to keep the pre-B37 behaviour
    /// of allocating a fresh <c>byte[]</c> for every large chunk.
    /// </summary>
    public Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndStreamChangedAsync(
        string filePath,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        MemoryBudget? memoryBudget,
        ChunkBufferPool? largeChunkPool,
        CancellationToken cancellationToken = default)
        => ChunkAndStreamChangedAsync(filePath, existingHashes, channel, cdcProgress,
            memoryBudget, largeChunkPool, smallChunkPool: null, cancellationToken);

    /// <summary>
    /// B69 overload: as above, plus an optional
    /// <see cref="ChunkBufferPool"/> (constructed with
    /// <see cref="ChunkBufferPool.SmallChunkBucketSizes"/>) that the
    /// producer rents from for chunks BELOW
    /// <see cref="PoolSkipThresholdBytes"/>. When supplied, the
    /// small-chunk path replaces <see cref="ArrayPool{T}.Shared"/>
    /// rentals with operation-scoped bucket-bag rentals, eliminating
    /// the per-core tier-cache residency that pre-B69 lived entirely
    /// outside the active <see cref="MemoryBudget"/>. Pass <c>null</c>
    /// to keep the pre-B69 behaviour of routing small chunks through
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public async Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndStreamChangedAsync(
        string filePath,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        MemoryBudget? memoryBudget,
        ChunkBufferPool? largeChunkPool,
        ChunkBufferPool? smallChunkPool,
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
                channel, cdcProgress, chunks, memoryBudget, largeChunkPool, smallChunkPool, cancellationToken);
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
        long payloadCharged = 0;
        bool payloadReturnToPool = false;
        ChunkBufferPool? payloadAssignedPool = null;
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

            // B30: rent (or allocate) the first chunk's payload buffer and
            // charge the budget for it. AcquireChunkBufferAsync awaits when
            // the budget is full, which is the throttling primitive that
            // keeps the producer side honest.
            (payloadBuffer, payloadCharged, payloadReturnToPool, payloadAssignedPool) =
                await AcquireChunkBufferAsync(config.MaxChunkSize, memoryBudget, largeChunkPool, smallChunkPool, cancellationToken);

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
                            await DispatchChunkAsync(chunk, payloadBuffer, payloadCharged,
                                payloadReturnToPool, payloadAssignedPool, chunks, existingHashes,
                                channel, cdcProgress, fileLength, memoryBudget, cancellationToken);
                            // Ownership transferred (or buffer + budget released
                            // inside DispatchChunkAsync on the dedup branch).
                            payloadBuffer = null;
                            payloadCharged = 0;
                            payloadAssignedPool = null;
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
                    await DispatchChunkAsync(chunk, payloadBuffer, payloadCharged,
                        payloadReturnToPool, payloadAssignedPool, chunks, existingHashes,
                        channel, cdcProgress, fileLength, memoryBudget, cancellationToken);

                    // Ownership of the buffer was transferred to the consumer
                    // (or returned to the pool + budget inside DispatchChunkAsync
                    // on the dedup branch). Reset for the next chunk.
                    chunkStart += chunkLength;
                    chunkLength = 0;
                    rollingHash = 0;
                    windowFilled = false;
                    windowPos = 0;
                    Array.Clear(window);

                    if (chunkStart >= fileLength)
                    {
                        payloadBuffer = null;
                        payloadCharged = 0;
                        payloadAssignedPool = null;
                        break;
                    }

                    // B30: charge the next chunk before allocating. This is
                    // where the budget binds for files with many large
                    // chunks -- the producer awaits here when the in-flight
                    // headroom is consumed by upstream consumers.
                    (payloadBuffer, payloadCharged, payloadReturnToPool, payloadAssignedPool) =
                        await AcquireChunkBufferAsync(config.MaxChunkSize, memoryBudget, largeChunkPool, smallChunkPool, cancellationToken);
                }
            }

            return (chunks, FinalizeFileHash(fileHasher));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            // payloadBuffer is null here on success (ownership transferred)
            // and non-null only if we threw mid-chunk before emitting. In
            // that case we still own the budget charge and must release
            // both the buffer and the charged bytes.
            if (payloadBuffer != null)
            {
                ReleaseChunkBuffer(payloadBuffer, payloadReturnToPool, payloadAssignedPool, payloadCharged, memoryBudget);
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
        long payloadCharged,
        bool payloadReturnToPool,
        ChunkBufferPool? payloadAssignedPool,
        List<ChunkInfo> chunks,
        HashSet<string> existingHashes,
        ChannelWriter<ChunkPayload> channel,
        IProgress<(long bytesProcessed, long totalBytes, int chunksFound)>? cdcProgress,
        long fileLength,
        MemoryBudget? memoryBudget,
        CancellationToken cancellationToken)
    {
        chunks.Add(chunk);

        if (existingHashes.Contains(chunk.Hash))
        {
            // Dedup branch: chunk is already in Azure. Drop the buffer and
            // release the producer-side budget charge so the next chunk's
            // AcquireChunkBufferAsync sees the headroom freed up. Without
            // this release the budget would leak chargedBytes per dedup
            // hit -- on a re-backup of an unchanged folder every chunk
            // would dedup and the budget would saturate at 0 remaining
            // after the first MaxParallel files even though no bytes are
            // actually in flight.
            chunk.BlobName = $"chunks/{chunk.Hash}";
            ReleaseChunkBuffer(payloadBuffer, payloadReturnToPool, payloadAssignedPool, payloadCharged, memoryBudget);
        }
        else
        {
            // Ownership transfers to the consumer. ChargedBytes,
            // ReturnToPool, and BufferPool ride along on the payload so
            // the consumer can mirror the producer's accounting and pool
            // decisions exactly.
            await channel.WriteAsync(
                new ChunkPayload(chunk, payloadBuffer, chunk.Length, payloadCharged, payloadReturnToPool, payloadAssignedPool),
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
        MemoryBudget? memoryBudget,
        ChunkBufferPool? largeChunkPool,
        ChunkBufferPool? smallChunkPool,
        CancellationToken cancellationToken)
    {
        var dataLength = (int)fileLength;
        // B30: charge the budget before allocating, even on the tiny-file
        // path. ArrayPool.Rent(0) throws so we always size at least 1.
        var rentSize = Math.Max(dataLength, 1);
        var (data, charged, returnToPool, assignedPool) = await AcquireChunkBufferAsync(
            rentSize, memoryBudget, largeChunkPool, smallChunkPool, cancellationToken);
        var transferred = false;
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
                await channel.WriteAsync(
                    new ChunkPayload(info, data, dataLength, charged, returnToPool, assignedPool),
                    cancellationToken);
                transferred = true;
            }
            else
            {
                info.BlobName = $"chunks/{info.Hash}";
            }
        }
        finally
        {
            if (!transferred)
            {
                ReleaseChunkBuffer(data, returnToPool, assignedPool, charged, memoryBudget);
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
