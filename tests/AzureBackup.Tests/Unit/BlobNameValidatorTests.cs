using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for BlobNameValidator hash validation.
/// </summary>
public class BlobNameValidatorTests
{
    #region ValidateChunkHash — valid hashes

    [Fact]
    public void WhenValidSha256HexThenDoesNotThrow()
    {
        // 64 hex chars = valid SHA-256
        var hash = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2";
        var exception = Record.Exception(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenAllZeroHashThenDoesNotThrow()
    {
        var hash = new string('0', 64);
        var exception = Record.Exception(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenLowercaseHexThenDoesNotThrow()
    {
        var hash = new string('a', 64);
        var exception = Record.Exception(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Null(exception);
    }

    #endregion

    #region ValidateChunkHash — invalid hashes

    [Fact]
    public void WhenNullThenThrowsSecurityPolicyException()
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(null!));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Fact]
    public void WhenEmptyThenThrowsSecurityPolicyException()
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(""));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Fact]
    public void WhenWhitespaceThenThrowsSecurityPolicyException()
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash("   "));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Fact]
    public void WhenTooShortThenThrowsSecurityPolicyException()
    {
        var hash = new string('A', 63);
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Fact]
    public void WhenTooLongThenThrowsSecurityPolicyException()
    {
        var hash = new string('A', 65);
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Fact]
    public void WhenContainsNonHexCharThenThrowsSecurityPolicyException()
    {
        var hash = new string('G', 64);
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(hash));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    [Theory]
    [InlineData("../../../etc/passwd" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("chunks/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void WhenPathTraversalAttemptThenThrowsSecurityPolicyException(string malicious)
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => BlobNameValidator.ValidateChunkHash(malicious));
        Assert.Equal(SecurityPolicyType.InvalidBlobName, ex.PolicyType);
    }

    #endregion
}
