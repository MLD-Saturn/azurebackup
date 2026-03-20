namespace AzureBackup.Core;

/// <summary>
/// Validates password strength for security-sensitive operations.
/// </summary>
public static class PasswordValidator
{
    /// <summary>
    /// Minimum password length required for new setups.
    /// </summary>
    public const int MinPasswordLength = 12;

    /// <summary>
    /// Validates that a password meets strength requirements.
    /// Requires minimum length and all four character types: uppercase, lowercase, digits, and special characters.
    /// </summary>
    /// <exception cref="SecurityPolicyException">Thrown when the password does not meet requirements.</exception>
    public static void Validate(string password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new SecurityPolicyException(
                $"Password must be at least {MinPasswordLength} characters long.",
                SecurityPolicyType.WeakPassword);
        }

        var hasUpper = password.Any(char.IsUpper);
        var hasLower = password.Any(char.IsLower);
        var hasDigit = password.Any(char.IsDigit);
        var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

        if (!hasUpper || !hasLower || !hasDigit || !hasSpecial)
        {
            throw new SecurityPolicyException(
                "Password must contain all of: uppercase, lowercase, digits, and special characters.",
                SecurityPolicyType.WeakPassword);
        }
    }
}
