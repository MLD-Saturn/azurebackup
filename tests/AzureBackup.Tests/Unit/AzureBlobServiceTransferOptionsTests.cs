using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B53 (W3 Phase B): tests for the chunk-size-gated upload-transfer-options
/// helper on <see cref="AzureBlobService"/>. The helper scales the SDK's
/// per-upload staging residency with the encrypted payload size; small
/// chunks pay only for what fits in one or two blocks while large chunks
/// keep the pre-B53 8x8 MB fan-out.
/// </summary>
public class AzureBlobServiceTransferOptionsTests
{
    private const int KB = 1024;
    private const int MB = 1024 * 1024;

    [Theory]
    [InlineData(1)]                  // 1 byte
    [InlineData(64 * KB)]            // 64 KB
    [InlineData(1 * MB)]             // 1 MB
    [InlineData(7 * MB)]             // 7 MB
    [InlineData(8 * MB)]             // 8 MB exact
    public void EncryptedLengthAtOrBelow8Mb_UsesSinglePut(int encryptedLength)
    {
        var options = AzureBlobService.ComputeUploadTransferOptions(encryptedLength);

        Assert.Equal(1, options.MaximumConcurrency);
        Assert.Equal(encryptedLength, options.MaximumTransferSize);
        Assert.Equal(encryptedLength, options.InitialTransferSize);
    }

    [Theory]
    [InlineData(8 * MB + 1)]
    [InlineData(12 * MB)]
    [InlineData(16 * MB)]
    public void EncryptedLengthBetween8MbAnd16Mb_UsesTwoBlocks(int encryptedLength)
    {
        var options = AzureBlobService.ComputeUploadTransferOptions(encryptedLength);

        Assert.Equal(2, options.MaximumConcurrency);
        Assert.Equal(8 * MB, options.MaximumTransferSize);
        Assert.Equal(8 * MB, options.InitialTransferSize);
    }

    [Theory]
    [InlineData(16 * MB + 1)]
    [InlineData(24 * MB)]
    [InlineData(32 * MB)]
    public void EncryptedLengthBetween16MbAnd32Mb_UsesFourBlocks(int encryptedLength)
    {
        var options = AzureBlobService.ComputeUploadTransferOptions(encryptedLength);

        Assert.Equal(4, options.MaximumConcurrency);
        Assert.Equal(8 * MB, options.MaximumTransferSize);
        Assert.Equal(8 * MB, options.InitialTransferSize);
    }

    [Theory]
    [InlineData(32 * MB + 1)]
    [InlineData(64 * MB)]
    [InlineData(128 * MB)]
    [InlineData(256 * MB)]
    public void EncryptedLengthAbove32Mb_UsesFullEightWayFanOut(int encryptedLength)
    {
        // Pre-B53 fan-out preserved verbatim for chunks that need it.
        var options = AzureBlobService.ComputeUploadTransferOptions(encryptedLength);

        Assert.Equal(8, options.MaximumConcurrency);
        Assert.Equal(8 * MB, options.MaximumTransferSize);
        Assert.Equal(8 * MB, options.InitialTransferSize);
    }

    [Fact]
    public void ZeroLength_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => AzureBlobService.ComputeUploadTransferOptions(0));
    }

    [Fact]
    public void NegativeLength_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => AzureBlobService.ComputeUploadTransferOptions(-1));
    }

    [Fact]
    public void StagingResidencyBound_TracksEncryptedLength()
    {
        // The whole point of B53 is that staging residency
        // (MaximumConcurrency * MaximumTransferSize) scales with the
        // chunk size rather than always being 64 MB. This test pins
        // the residency at each band so a future regression that
        // re-broadens the helper (e.g. removing the small-chunk
        // single-PUT path) fails loudly.
        long Staging(int n) =>
            (long)AzureBlobService.ComputeUploadTransferOptions(n).MaximumConcurrency!
            * AzureBlobService.ComputeUploadTransferOptions(n).MaximumTransferSize!.Value;

        Assert.Equal(1L * MB, Staging(1 * MB));        // 1x1
        Assert.Equal(8L * MB, Staging(8 * MB));        // 1x8
        Assert.Equal(16L * MB, Staging(16 * MB));      // 2x8
        Assert.Equal(32L * MB, Staging(32 * MB));      // 4x8
        Assert.Equal(64L * MB, Staging(64 * MB));      // 8x8 (unchanged from pre-B53)
    }

    // ---- B55 (W3 Phase D): EstimateUploadStagingBytes ----

    [Theory]
    [InlineData(1 * MB, 1L * MB)]
    [InlineData(8 * MB, 8L * MB)]
    [InlineData(16 * MB, 16L * MB)]
    [InlineData(32 * MB, 32L * MB)]
    [InlineData(64 * MB, 64L * MB)]
    [InlineData(128 * MB, 64L * MB)]
    [InlineData(256 * MB, 64L * MB)]
    public void EstimateUploadStagingBytes_MatchesTransferOptionsProduct(int encryptedLength, long expected)
    {
        // The estimate must equal MaximumConcurrency * MaximumTransferSize
        // from the same helper, so the producer-side budget charge in
        // ChunkingService cannot drift away from the actual SDK staging
        // shape used at the upload site.
        var actual = AzureBlobService.EstimateUploadStagingBytes(encryptedLength);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EstimateUploadStagingBytes_ZeroLength_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => AzureBlobService.EstimateUploadStagingBytes(0));
    }

    [Fact]
    public void EstimateUploadStagingBytes_NegativeLength_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => AzureBlobService.EstimateUploadStagingBytes(-1));
    }
}
