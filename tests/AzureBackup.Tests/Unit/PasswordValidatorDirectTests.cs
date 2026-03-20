using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Direct unit tests for PasswordValidator.
/// These test the extracted validator in isolation without requiring BackupOrchestrator or database setup.
/// </summary>
public class PasswordValidatorDirectTests
{
    #region Length validation

    [Theory]
    [InlineData("Short1!")]
    [InlineData("shortshor1!")]
    [InlineData("Aa1!")]
    public void WhenPasswordTooShortThenThrowsWeakPasswordException(string password)
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => PasswordValidator.Validate(password));
        Assert.Equal(SecurityPolicyType.WeakPassword, ex.PolicyType);
        Assert.Contains($"{PasswordValidator.MinPasswordLength} characters", ex.Message);
    }

    #endregion

    #region Character type validation

    [Theory]
    [InlineData("alllowercase1234")]
    [InlineData("ALLUPPERCASE1234")]
    [InlineData("NoDigitsHere!@##")]
    [InlineData("!!!!!!!!!!!!")]
    [InlineData("MySecurePass123")]
    [InlineData("mysecurepass1!")]
    [InlineData("MYSECUREPASS1!")]
    [InlineData("MySecurePass!@#")]
    public void WhenPasswordMissingCharacterTypeThenThrowsWeakPasswordException(string password)
    {
        var ex = Assert.Throws<SecurityPolicyException>(() => PasswordValidator.Validate(password));
        Assert.Equal(SecurityPolicyType.WeakPassword, ex.PolicyType);
        Assert.Contains("all of", ex.Message);
    }

    #endregion

    #region Valid passwords

    [Theory]
    [InlineData("StrongPassword1!")]
    [InlineData("SECURE!pass123")]
    [InlineData("Complex!Password1")]
    [InlineData("MyP@ssw0rd1234")]
    [InlineData("Aa1!Aa1!Aa1!")]
    public void WhenPasswordMeetsAllRequirementsThenDoesNotThrow(string password)
    {
        var exception = Record.Exception(() => PasswordValidator.Validate(password));
        Assert.Null(exception);
    }

    #endregion

    #region Boundary

    [Fact]
    public void WhenPasswordExactlyMinLengthAndStrongThenDoesNotThrow()
    {
        // Exactly 12 characters with all 4 types
        var password = "Abcdefghij1!";
        Assert.Equal(PasswordValidator.MinPasswordLength, password.Length);

        var exception = Record.Exception(() => PasswordValidator.Validate(password));
        Assert.Null(exception);
    }

    [Fact]
    public void WhenPasswordOneBelowMinLengthThenThrows()
    {
        // 11 characters with all 4 types
        var password = "Abcdefghi1!";
        Assert.Equal(PasswordValidator.MinPasswordLength - 1, password.Length);

        Assert.Throws<SecurityPolicyException>(() => PasswordValidator.Validate(password));
    }

    #endregion
}
