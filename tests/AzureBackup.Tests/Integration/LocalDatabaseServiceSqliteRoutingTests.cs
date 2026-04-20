using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1 final step b: end-to-end proof that setting the
/// <c>AZBK_USE_SQLITE</c> environment variable routes
/// <see cref="LocalDatabaseService"/> through the SQLite backend instead
/// of LiteDB.
///
/// <para>
/// We do NOT re-test SQLite correctness here - that's covered by the
/// contract tests + direct-SqliteBackend tests from C-1 and C-3. This
/// file verifies ONLY the routing: that with the flag set every call
/// hits the backend and the LiteDB collections stay null.
/// </para>
///
/// <para>
/// xUnit runs each test class on its own thread but the process
/// environment is shared. Every test uses the <c>EnvVarScope</c>
/// helper to snapshot+restore on dispose so a test that flips the flag
/// cannot leak into later tests.
/// </para>
/// </summary>
public class LocalDatabaseServiceSqliteRoutingTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public LocalDatabaseServiceSqliteRoutingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "routed.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initialize_WithFlagSet_RoundTripsThroughSqliteBackend()
    {
        using var _flag = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, "1");

        using var service = new LocalDatabaseService();
        service.Initialize(_dbPath, "RoutingTestPassword!".AsSpan());

        // The DB file should now exist at _dbPath. If we had gone down
        // the LiteDB path the file would also exist - so file-existence
        // alone is not proof. The definitive proof is a behaviour the
        // two backends differ on:
        //   * LiteDB uses a ".litedb-log" companion during writes.
        //   * SQLite uses a "-wal" companion during writes.
        // We do a write and then check for the "-wal" companion.
        service.SetIndexMetadata("RoutingProbe", DateTime.UtcNow);

        Assert.True(File.Exists(_dbPath + "-wal"),
            "Expected a SQLite WAL companion file next to the DB, meaning the SQLite backend wrote pages. None found - routing is broken.");

        // Round-trip: value goes in, same service instance reads it back.
        var readBack = service.GetIndexMetadata("RoutingProbe");
        Assert.NotNull(readBack);
    }

    [Fact]
    public void Initialize_WithoutFlag_UsesLiteDbPath()
    {
        using var _flag = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, null);

        using var service = new LocalDatabaseService();
        service.Initialize(_dbPath, "RoutingTestPassword!".AsSpan());
        service.SetIndexMetadata("RoutingProbe", DateTime.UtcNow);

        // LiteDB writes "-log" not "-wal".
        Assert.False(File.Exists(_dbPath + "-wal"),
            "Expected no SQLite WAL file - LiteDB should not create one.");

        // Sanity round-trip.
        var readBack = service.GetIndexMetadata("RoutingProbe");
        Assert.NotNull(readBack);
    }

    [Fact]
    public void FullBackedUpFileRoundTrip_ViaSqliteBackend()
    {
        using var _flag = new EnvVarScope(DatabaseBackendFactory.EnvironmentVariableName, "1");

        using var service = new LocalDatabaseService();
        service.Initialize(_dbPath, "RoutingTestPassword!".AsSpan());

        var toSave = new BackedUpFile
        {
            LocalPath = @"C:\routing\target.bin",
            BlobName = "metadata/target.bin.json",
            FileSize = 4096,
            LastModified = DateTime.UtcNow,
            FileHash = "ROUTEHASH",
            Status = BackupStatus.Completed,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
        };
        toSave.Chunks.Add(new ChunkInfo
        {
            Index = 0,
            Offset = 0,
            Length = 4096,
            Hash = "CHUNK-000",
            BlobName = "chunks/CHUNK-000",
        });

        service.SaveBackedUpFile(toSave);

        var readBack = service.GetBackedUpFile(@"C:\routing\target.bin");
        Assert.NotNull(readBack);
        Assert.Equal("ROUTEHASH", readBack!.FileHash);
        Assert.Single(readBack.Chunks);
        Assert.Equal("CHUNK-000", readBack.Chunks[0].Hash);
    }

    /// <summary>
    /// Snapshot-and-restore helper identical to the one in
    /// <c>DatabaseBackendFactoryTests</c>. Kept as a duplicate (one per
    /// test file) because xUnit discourages shared mutable infrastructure
    /// and 8 lines is not worth a shared fixture.
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
