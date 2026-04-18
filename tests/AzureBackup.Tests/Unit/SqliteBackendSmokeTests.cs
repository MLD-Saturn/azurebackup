using AzureBackup.Core;
using AzureBackup.Core.Services;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1a: end-to-end smoke test for the SQLite + SQLCipher
/// integration. Proves the encryption stack works on .NET 10 before any
/// real persistence code is written against it.
///
/// <para>
/// What is tested:
/// </para>
/// <list type="bullet">
///   <item>Open an encrypted DB at a fresh path - schema is created, the
///     connection is usable, a salt file appears.</item>
///   <item>Close, reopen with the same password - data is readable, the
///     schema persists.</item>
///   <item>Reopen with the wrong password - throws
///     <see cref="InvalidPasswordException"/> rather than silently returning
///     an empty / corrupt DB.</item>
/// </list>
///
/// <para>
/// These are intentionally small assertions; the full functional surface is
/// proved later by the existing 536 tests once the backend is wired into
/// <c>LocalDatabaseService</c>.
/// </para>
/// </summary>
public class SqliteBackendSmokeTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public SqliteBackendSmokeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "smoke.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initialize_NewDatabase_CreatesSchemaAndSaltFile()
    {
        // Arrange
        using var backend = new SqliteBackend();
        const string password = "SmokeTestPassword123!";

        // Act
        backend.Initialize(_dbPath, password.AsSpan());

        // Assert
        Assert.True(backend.IsInitialized);
        Assert.Equal(_dbPath, backend.DatabasePath);
        Assert.True(File.Exists(_dbPath), "Database file should exist on disk");
        Assert.True(File.Exists(_dbPath + ".salt"), "Salt file should be created next to the database");

        // SQLite version is reachable -> connection is alive and decrypted.
        var version = backend.ReadSqliteVersion();
        Assert.False(string.IsNullOrEmpty(version), "Should be able to read sqlite_version()");

        // Schema was created. We expect every table from CreateSchema().
        var tableCount = backend.CountSchemaTables();
        Assert.Equal(10, tableCount);
    }

    [Fact]
    public void Initialize_ReopenWithSamePassword_Succeeds()
    {
        // Arrange
        const string password = "SmokeTestPassword123!";

        using (var first = new SqliteBackend())
        {
            first.Initialize(_dbPath, password.AsSpan());
            Assert.Equal(10, first.CountSchemaTables());
            // first.Dispose() runs here, closing the connection.
        }

        // Act: reopen with the same password.
        using var second = new SqliteBackend();
        second.Initialize(_dbPath, password.AsSpan());

        // Assert: schema persisted, no exception, connection works.
        Assert.True(second.IsInitialized);
        Assert.Equal(10, second.CountSchemaTables());
    }

    [Fact]
    public void Initialize_NewDatabase_LoadedNativeIsSqlcipher()
    {
        // Critical guard: if the wrong native bundle ships, encryption is
        // silently a no-op and the wrong-password test below would pass
        // for the wrong reason. PRAGMA cipher_version returns null on a
        // plain SQLite build and the SQLCipher version string on a
        // SQLCipher build.
        using var backend = new SqliteBackend();
        backend.Initialize(_dbPath, "ProveSqlcipherIsLoaded".AsSpan());

        var version = backend.ReadSqlcipherVersion();
        Assert.False(string.IsNullOrEmpty(version),
            "PRAGMA cipher_version returned empty - the loaded native library is plain SQLite, not SQLCipher. " +
            "Encryption would silently be a no-op. Check that SQLitePCLRaw.bundle_e_sqlcipher is referenced.");
    }

    [Fact]
    public void Initialize_WrongPassword_ProducesDifferentEncryption()
    {
        // Diagnostic: prove SQLCipher is at least using the key by showing
        // that two different passwords produce different on-disk bytes for
        // the same logical content. If this passes but the wrong-password
        // test fails, the issue is detection, not encryption.
        const string password1 = "Password1";
        const string password2 = "Password2";
        var db1 = Path.Combine(_testDir, "p1.db");
        var db2 = Path.Combine(_testDir, "p2.db");

        using (var b1 = new SqliteBackend()) b1.Initialize(db1, password1.AsSpan());
        using (var b2 = new SqliteBackend()) b2.Initialize(db2, password2.AsSpan());

        var bytes1 = File.ReadAllBytes(db1);
        var bytes2 = File.ReadAllBytes(db2);

        Assert.Equal(bytes1.Length, bytes2.Length);
        // First 16 bytes are the SQLCipher salt (random per-DB, so they'll
        // differ regardless of password). Compare bytes 16..32 which are
        // page-1 header content - encrypted, so they must differ when keys
        // differ.
        Assert.False(bytes1.AsSpan(16, 16).SequenceEqual(bytes2.AsSpan(16, 16)),
            "Page-1 ciphertext is identical between two passwords - encryption is not actually keyed");
    }

    [Fact]
    public void Initialize_ReopenWithWrongPassword_ThrowsInvalidPassword()
    {
        // Arrange
        const string correctPassword = "CorrectPassword123!";
        const string wrongPassword = "WrongPassword456!";

        using (var first = new SqliteBackend())
        {
            first.Initialize(_dbPath, correctPassword.AsSpan());
            // Sanity check: confirm SQLCipher is actually doing the encryption.
            // Without this the wrong-password test could pass for the wrong
            // reason (plain SQLite would happily open any byte stream as a DB).
            Assert.False(string.IsNullOrEmpty(first.ReadSqlcipherVersion()));
        }

        // Act + Assert: SQLCipher must reject the wrong key on first read
        // rather than silently returning an empty DB.
        using var second = new SqliteBackend();
        var ex = Record.Exception(() =>
            second.Initialize(_dbPath, wrongPassword.AsSpan()));

        Assert.NotNull(ex);
        // If this fires it tells us what we actually got vs what we expected.
        Assert.True(ex is InvalidPasswordException,
            $"Expected InvalidPasswordException, got {ex.GetType().Name}: {ex.Message}");
    }
}
