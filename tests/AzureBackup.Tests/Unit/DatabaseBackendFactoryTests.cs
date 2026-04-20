using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1 final step b: unit tests for the feature-flag
/// resolver. Tests IsTruthy with a parameter matrix so we document
/// exactly which env-var values enable SQLite.
///
/// <para>
/// ShouldUseSqlite itself reads process environment, which is shared
/// across tests in the same AppDomain. xUnit runs test classes in
/// isolated collections so a per-test env-var flip is safe as long
/// as we restore the value on teardown.
/// </para>
/// </summary>
public class DatabaseBackendFactoryTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("  true  ", true)]   // whitespace tolerated
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("enabled", false)]    // NOT accepted - only the documented tokens
    [InlineData("2", false)]
    [InlineData("t", false)]
    [InlineData("y", false)]
    public void IsTruthy_RecognisesExpectedTokens(string? input, bool expected)
    {
        Assert.Equal(expected, DatabaseBackendFactory.IsTruthy(input));
    }

    [Fact]
    public void ShouldUseSqlite_UnsetEnvVar_ReturnsFalse()
    {
        using var _ = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, null);
        Assert.False(DatabaseBackendFactory.ShouldUseSqlite());
    }

    [Fact]
    public void ShouldUseSqlite_EnvVarSetToOne_ReturnsTrue()
    {
        using var _ = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, "1");
        Assert.True(DatabaseBackendFactory.ShouldUseSqlite());
    }

    [Fact]
    public void ShouldUseSqlite_EnvVarSetToFalse_ReturnsFalse()
    {
        using var _ = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, "false");
        Assert.False(DatabaseBackendFactory.ShouldUseSqlite());
    }

    /// <summary>
    /// Snapshot-and-restore helper so tests that touch environment
    /// variables don't leak into each other.
    /// </summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvVarScope(string name, string? newValue)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, newValue);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable(_name, _previousValue);
    }
}
