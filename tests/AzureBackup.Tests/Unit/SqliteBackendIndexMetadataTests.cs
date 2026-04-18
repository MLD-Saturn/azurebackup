using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1b: round-trip tests for the SQLite backend's
/// <c>index_metadata</c> key/value table. This is the smallest persistent
/// surface and the first one ported over from LiteDB so we can validate
/// the read-write contract before tackling more complex tables.
///
/// <para>
/// Mirrors the assertions in <c>LocalDatabaseServiceTests</c> for index
/// metadata - same inputs, same expected outputs - so a future migration
/// commit can run both backends through the same logical assertions.
/// </para>
/// </summary>
public class SqliteBackendIndexMetadataTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendIndexMetadataTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "meta.db");
        _backend = new SqliteBackend();
        _backend.Initialize(_dbPath, "MetaTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetIndexMetadata_MissingKey_ReturnsNull()
    {
        var result = _backend.GetIndexMetadata("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public void SetIndexMetadata_RoundTripsValueAsUtc()
    {
        // Arrange: a UTC value with sub-second precision.
        var key = "ReverseIndexBuiltAt";
        var value = new DateTime(2026, 4, 17, 21, 30, 45, 123, DateTimeKind.Utc);

        // Act
        _backend.SetIndexMetadata(key, value);
        var read = _backend.GetIndexMetadata(key);

        // Assert
        Assert.NotNull(read);
        Assert.Equal(DateTimeKind.Utc, read.Value.Kind);
        Assert.Equal(value, read.Value);
    }

    [Fact]
    public void SetIndexMetadata_LocalValue_PersistedAsUtc()
    {
        // Arrange: a local-kind value. The contract is that we always store
        // UTC so cross-time-zone reads are deterministic.
        var key = "LocalToUtc";
        var localValue = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);

        // Act
        _backend.SetIndexMetadata(key, localValue);
        var read = _backend.GetIndexMetadata(key);

        // Assert: equal as instants, kind normalised to UTC.
        Assert.NotNull(read);
        Assert.Equal(DateTimeKind.Utc, read.Value.Kind);
        Assert.Equal(localValue.ToUniversalTime(), read.Value);
    }

    [Fact]
    public void SetIndexMetadata_OverwritesPreviousValue()
    {
        // Arrange
        var key = "Overwrite";
        var first = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // Act
        _backend.SetIndexMetadata(key, first);
        _backend.SetIndexMetadata(key, second);

        // Assert
        Assert.Equal(second, _backend.GetIndexMetadata(key));
    }

    [Fact]
    public void SetIndexMetadata_SurvivesReopen()
    {
        // Arrange: write before close.
        var key = "Persistent";
        var value = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        _backend.SetIndexMetadata(key, value);
        _backend.Dispose();

        // Act: reopen and read.
        using var reopened = new SqliteBackend();
        reopened.Initialize(_dbPath, "MetaTestPwd!".AsSpan());

        // Assert
        Assert.Equal(value, reopened.GetIndexMetadata(key));
    }

    [Fact]
    public void Checkpoint_DoesNotThrow()
    {
        _backend.SetIndexMetadata("BeforeCheckpoint",
            new DateTime(2026, 4, 17, 0, 0, 0, DateTimeKind.Utc));
        _backend.Checkpoint();
        // Re-read to confirm checkpoint did not corrupt the row.
        Assert.NotNull(_backend.GetIndexMetadata("BeforeCheckpoint"));
    }

    [Fact]
    public void GetIndexMetadata_NullOrWhitespaceKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => _backend.GetIndexMetadata(""));
        Assert.Throws<ArgumentException>(() => _backend.GetIndexMetadata("   "));
    }

    [Fact]
    public void SetIndexMetadata_NullOrWhitespaceKey_Throws()
    {
        var v = DateTime.UtcNow;
        Assert.Throws<ArgumentException>(() => _backend.SetIndexMetadata("", v));
        Assert.Throws<ArgumentException>(() => _backend.SetIndexMetadata("   ", v));
    }
}
