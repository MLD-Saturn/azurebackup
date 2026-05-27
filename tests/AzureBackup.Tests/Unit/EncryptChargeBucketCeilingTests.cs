using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B74 (W5 Phase 4 Commit 3, Fix B): pins the charge-vs-rent invariant
/// for the small-pool encrypted-buffer path that B73 introduced.
///
/// <para>
/// Pre-B74 the producer-side encrypt-buffer charge in
/// <c>ChunkingService.AcquireChunkBufferAsync</c> used
/// <see cref="ChunkingService.NextPowerOfTwoOrSelf"/>, which matches
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/>'s power-of-two tier
/// shape but UNDER-charges by up to 2x against the small
/// <see cref="ChunkBufferPool"/>'s [64K, 256K, 1M, 4M, 16M] bucket geometry.
/// Concrete case: a 4 MB plaintext chunk encrypts to ~4 MB + 37 bytes,
/// which the small pool rents at 16 MB but the pre-B74 charge computed
/// as 8 MB -- an 8 MB per-chunk under-charge that could accumulate to
/// multi-GB at 96-way in-flight worst case.
/// </para>
///
/// <para>
/// These tests pin <see cref="ChunkingService.BucketCeiling"/> against
/// the actual rent of a small <see cref="ChunkBufferPool"/> for every
/// boundary size the production workloads exercise, plus the
/// <see cref="ChunkingService.NextPowerOfTwoOrSelf"/> contract for the
/// ArrayPool fallback / large-pool path that B74's C2 keeps on
/// ArrayPool routing.
/// </para>
/// </summary>
public sealed class EncryptChargeBucketCeilingTests
{
    private const int MB = 1024 * 1024;
    private const int KB = 1024;
    private const int EncryptionOverhead = 37;

    [Theory]
    // Adversarial-pool-churn deliberately probes every small-pool boundary.
    [InlineData(64 * KB + 1)]      // 64 KB + 1 byte
    [InlineData(85_001)]            // LOH threshold + 1
    [InlineData(256 * KB + 1)]     // 256 KB + 1
    [InlineData(1 * MB + 1)]       // 1 MB + 1
    [InlineData(2 * MB)]           // mid-bucket
    [InlineData(4 * MB + 1)]       // 4 MB + 1 -- worst case (8 MB under-charge pre-B74)
    [InlineData(8 * MB)]           // mid-bucket
    [InlineData(16 * MB - EncryptionOverhead - 1)] // largest plaintext that still routes to small pool
    public void BucketCeilingMatchesActualSmallPoolRent(int plaintextSize)
    {
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var encryptedSize = plaintextSize + EncryptionOverhead;
        var charge = ChunkingService.BucketCeiling(encryptedSize, pool);
        var rent = pool.Rent(encryptedSize).Buffer;

        try
        {
            Assert.Equal(rent.Length, charge);
        }
        finally
        {
            pool.Return(rent);
        }
    }

    [Theory]
    // Large-pool sizes (post-B74 C2 these go through ArrayPool, so the
    // producer charge is NextPowerOfTwoOrSelf and the rent is ArrayPool's
    // power-of-two tier -- the two MUST line up).
    [InlineData(16 * MB)]
    [InlineData(20 * MB)]
    [InlineData(32 * MB)]
    [InlineData(64 * MB)]
    [InlineData(128 * MB)]
    public void NextPowerOfTwoOrSelfIsAtLeastTheRequest(int request)
    {
        // The rent on ArrayPool<byte>.Shared can vary across runtimes
        // (the BCL's actual bucket size is an implementation detail),
        // but the charge MUST be at least the request -- otherwise the
        // budget would systematically undercount the encrypted buffer
        // residency on the large-chunk path that B74's C2 routes to
        // ArrayPool. The pin is "never under-charge" rather than
        // "exactly equal the rent" because over-charge by one tier is
        // the documented safe-by-default position.
        var charge = ChunkingService.NextPowerOfTwoOrSelf(request);
        Assert.True(charge >= request, $"charge {charge} < request {request}");
    }

    [Fact]
    public void NextPowerOfTwoOrSelfReturnsExactValueForPowerOfTwo()
    {
        // A power-of-two input MUST return itself (no needless rounding-up).
        Assert.Equal(64 * KB, ChunkingService.NextPowerOfTwoOrSelf(64 * KB));
        Assert.Equal(1 * MB, ChunkingService.NextPowerOfTwoOrSelf(1 * MB));
        Assert.Equal(64 * MB, ChunkingService.NextPowerOfTwoOrSelf(64 * MB));
    }

    [Fact]
    public void NextPowerOfTwoOrSelfRoundsUpForNonPowerOfTwo()
    {
        // The 37-byte AES-GCM envelope guarantees encrypted sizes are
        // never powers of two even when the plaintext is; the pin
        // confirms the round-up matches the next ArrayPool tier.
        Assert.Equal(128 * KB, ChunkingService.NextPowerOfTwoOrSelf(64 * KB + 1));
        Assert.Equal(2 * MB, ChunkingService.NextPowerOfTwoOrSelf(1 * MB + EncryptionOverhead));
        Assert.Equal(128 * MB, ChunkingService.NextPowerOfTwoOrSelf(64 * MB + EncryptionOverhead));
    }

    [Theory]
    // The pre-B74 bug shape: NextPow2 of these encrypted sizes is
    // 2x smaller than the small-pool actual rent. The test is a
    // direct quantification of the under-charge size so a future
    // change that re-introduces the bug fails with a clear delta.
    [InlineData(1 * MB + 1, 4 * MB, 2 * MB)]   // rent 4 MB, NextPow2 2 MB, delta 2 MB
    [InlineData(4 * MB + 1, 16 * MB, 8 * MB)]  // rent 16 MB, NextPow2 8 MB, delta 8 MB
    public void SmallPoolBucketCeilingExceedsNextPow2InGapZones(
        int plaintextSize, int expectedBucketSize, int expectedNextPow2)
    {
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var encryptedSize = plaintextSize + EncryptionOverhead;
        var bucketCeiling = ChunkingService.BucketCeiling(encryptedSize, pool);
        var nextPow2 = ChunkingService.NextPowerOfTwoOrSelf(encryptedSize);

        Assert.Equal(expectedBucketSize, bucketCeiling);
        Assert.Equal(expectedNextPow2, nextPow2);
        Assert.True(bucketCeiling > nextPow2,
            $"BucketCeiling {bucketCeiling} must exceed NextPow2 {nextPow2} in the gap zone");
    }
}
