using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-2: end-to-end tests for the LiteDB-to-SQLite migration
/// that runs inside <see cref="LocalDatabaseService.Initialize"/> when
/// <c>AZBK_USE_SQLITE</c> is set and an existing LiteDB database is
/// detected at the target path.
///
/// <para>
/// These tests exercise the migration invisibly through the public
/// <see cref="LocalDatabaseService.Initialize"/> surface. No direct
/// calls to <c>MigrateFromLiteDb</c>: the detection-and-dispatch logic
/// inside Initialize IS what we need to verify.
/// </para>
/// </summary>
public class LocalDatabaseServiceMigrationTests : IDisposable
{
    private const string Password = "MigrationTestPassword1!";

    private readonly string _testDir;
    private readonly string _dbPath;

    public LocalDatabaseServiceMigrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Migration_CopiesEveryCollection_AndSwapsFiles()
    {
        // Arrange: populate LiteDB with one row in every collection
        // the migration copies. Uses the flag-off code path so this is
        // a genuine LiteDB database at _dbPath.
        using (var _flagOff = new BackendOverrideScope(useSqlite: false))
        using (var seed = new LocalDatabaseService())
        {
            seed.Initialize(_dbPath, Password.AsSpan());

            seed.SaveConfiguration(new BackupConfiguration
            {
                Id = 1,
                StorageAccountName = "samplesa",
                ContainerName = "backups",
                LastBackupTime = new DateTime(2026, 4, 18, 10, 0, 0, DateTimeKind.Utc),
                TotalBytesUploaded = 12345,
            });

            seed.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = "CHUNK-AAA",
                FirstUploadedAt = new DateTime(2026, 4, 18, 10, 1, 0, DateTimeKind.Utc),
                SizeBytes = 1024,
                ReferenceCount = 1,
                LastVerifiedAt = new DateTime(2026, 4, 18, 10, 2, 0, DateTimeKind.Utc),
            });
            seed.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = "CHUNK-BBB",
                FirstUploadedAt = new DateTime(2026, 4, 18, 10, 1, 0, DateTimeKind.Utc),
                SizeBytes = 2048,
                ReferenceCount = 2,
                LastVerifiedAt = new DateTime(2026, 4, 18, 10, 2, 0, DateTimeKind.Utc),
            });

            var file = new BackedUpFile
            {
                LocalPath = @"C:\mig\doc.bin",
                BlobName = "metadata/doc.bin.json",
                FileSize = 3072,
                LastModified = new DateTime(2026, 4, 18, 9, 30, 0, DateTimeKind.Utc),
                FileHash = "FILEHASH1",
                Status = BackupStatus.Completed,
                BackedUpAt = new DateTime(2026, 4, 18, 9, 45, 0, DateTimeKind.Utc),
                MetadataVersion = 1,
            };
            file.Chunks.Add(new ChunkInfo { Index = 0, Offset = 0, Length = 1024, Hash = "CHUNK-AAA", BlobName = "chunks/CHUNK-AAA" });
            file.Chunks.Add(new ChunkInfo { Index = 1, Offset = 1024, Length = 2048, Hash = "CHUNK-BBB", BlobName = "chunks/CHUNK-BBB" });
            seed.SaveBackedUpFile(file);

            seed.QueueFileChange(new FileChangeEvent
            {
                FilePath = @"C:\mig\pending.bin",
                ChangeType = FileChangeType.Modified,
                DetectedAt = new DateTime(2026, 4, 18, 11, 0, 0, DateTimeKind.Utc),
            });

            seed.SetIndexMetadata("LastScan", new DateTime(2026, 4, 18, 11, 30, 0, DateTimeKind.Utc));
        }

        // Sanity: LiteDB database + salt exist at _dbPath.
        Assert.True(File.Exists(_dbPath), "LiteDB seed did not produce a database file");
        Assert.True(File.Exists(_dbPath + ".salt"), "LiteDB seed did not produce a salt file");

        // Act: flip the flag and open the database. Initialize should
        // detect the LiteDB file, run migration, and leave us with a
        // working SQLite database at _dbPath.
        using (var _flagOn = new BackendOverrideScope(useSqlite: true))
        using (var migrated = new LocalDatabaseService())
        {
            migrated.Initialize(_dbPath, Password.AsSpan());

            // Assert: the LiteDB database was renamed out of the way.
            Assert.True(File.Exists(_dbPath + ".litedb-backup"),
                "Migration should have renamed LiteDB to .litedb-backup");
            Assert.True(File.Exists(_dbPath + ".litedb-backup.salt"),
                "Migration should have renamed LiteDB salt to .litedb-backup.salt");

            // Assert: the SQLite database + salt are at the target path.
            Assert.True(File.Exists(_dbPath), "SQLite database should now be at the target path");
            Assert.True(File.Exists(_dbPath + ".salt"), "SQLite salt should now be at the target path");

            // Assert: every row we seeded round-tripped.
            var config = migrated.GetConfiguration();
            Assert.Equal("samplesa", config.StorageAccountName);
            Assert.Equal("backups", config.ContainerName);
            Assert.Equal(12345, config.TotalBytesUploaded);

            var allChunks = migrated.GetAllChunkIndexEntries();
            Assert.Equal(2, allChunks.Count);
            Assert.Contains(allChunks, e => e.ChunkHash == "CHUNK-AAA" && e.SizeBytes == 1024);
            Assert.Contains(allChunks, e => e.ChunkHash == "CHUNK-BBB" && e.SizeBytes == 2048);

            var readBack = migrated.GetBackedUpFile(@"C:\mig\doc.bin");
            Assert.NotNull(readBack);
            Assert.Equal("FILEHASH1", readBack!.FileHash);
            Assert.Equal(2, readBack.Chunks.Count);
            Assert.Equal("CHUNK-AAA", readBack.Chunks[0].Hash);
            Assert.Equal("CHUNK-BBB", readBack.Chunks[1].Hash);

            var pending = migrated.GetPendingChanges();
            Assert.Single(pending);
            Assert.Equal(@"C:\mig\pending.bin", pending[0].FilePath);

            var lastScan = migrated.GetIndexMetadata("LastScan");
            Assert.NotNull(lastScan);

            // Reverse index should be marked built so GetChunkEntriesForFile
            // works immediately post-migration.
            Assert.True(migrated.IsReverseChunkIndexBuilt());
            var chunksForFile = migrated.GetChunkEntriesForFile(@"C:\mig\doc.bin");
            Assert.Equal(2, chunksForFile.Count);
        }
    }

    [Fact]
    public void Migration_IsIdempotent_SecondOpenIsAPlainSqliteOpen()
    {
        // Arrange: seed LiteDB.
        using (var _flagOff = new BackendOverrideScope(useSqlite: false))
        using (var seed = new LocalDatabaseService())
        {
            seed.Initialize(_dbPath, Password.AsSpan());
            seed.SetIndexMetadata("IdempotentProbe", DateTime.UtcNow);
        }

        // First flag-on open: migrates.
        using (var _flagOn = new BackendOverrideScope(useSqlite: true))
        using (var first = new LocalDatabaseService())
        {
            first.Initialize(_dbPath, Password.AsSpan());
        }

        // Capture the SQLite file's modification time after migration.
        var afterMigration = File.GetLastWriteTimeUtc(_dbPath);

        // Sleep long enough that a re-migration would register a
        // different mtime. FAT-based filesystems have 2-second mtime
        // granularity; NTFS has finer but we use 50 ms to be safe
        // without making the test sluggish.
        Thread.Sleep(50);

        // Second flag-on open: should NOT migrate (the file is already SQLite).
        using (var _flagOn = new BackendOverrideScope(useSqlite: true))
        using (var second = new LocalDatabaseService())
        {
            second.Initialize(_dbPath, Password.AsSpan());

            // The .litedb-backup from the first migration should still
            // be there (we never delete it) but NO second backup should
            // have been created. If migration ran twice we would see
            // <path>.litedb-backup.litedb-backup, which must NOT exist.
            Assert.False(File.Exists(_dbPath + ".litedb-backup.litedb-backup"),
                "Second open should NOT have re-migrated");

            // Sanity round-trip still works.
            var readBack = second.GetIndexMetadata("IdempotentProbe");
            Assert.NotNull(readBack);
        }
    }

    [Fact]
    public void Migration_WrongPassword_ThrowsInvalidPasswordException_WithoutTouchingOriginal()
    {
        // Arrange: seed LiteDB with the real password.
        using (var _flagOff = new BackendOverrideScope(useSqlite: false))
        using (var seed = new LocalDatabaseService())
        {
            seed.Initialize(_dbPath, Password.AsSpan());
            seed.SetIndexMetadata("WrongPasswordProbe", DateTime.UtcNow);
        }

        var litedbMtime = File.GetLastWriteTimeUtc(_dbPath);

        // Act: flip the flag and attempt to open with a WRONG password.
        using var _flagOn = new BackendOverrideScope(useSqlite: true);
        using var attempt = new LocalDatabaseService();

        Assert.Throws<InvalidPasswordException>(() =>
            attempt.Initialize(_dbPath, "WrongPassword42!".AsSpan()));

        // Assert: the LiteDB file was NOT renamed (migration did not run
        // because the probe never saw a usable password for LiteDB open).
        Assert.True(File.Exists(_dbPath), "LiteDB should still exist");
        Assert.False(File.Exists(_dbPath + ".litedb-backup"),
            "Migration should NOT have run on wrong-password attempt");

        // Mtime unchanged (or very close - no write happened).
        Assert.Equal(litedbMtime, File.GetLastWriteTimeUtc(_dbPath));

        // Next attempt with the right password should still be able to
        // migrate (the DB is unchanged).
        using var retry = new LocalDatabaseService();
        retry.Initialize(_dbPath, Password.AsSpan());
        Assert.True(File.Exists(_dbPath + ".litedb-backup"),
            "Retry with correct password should have migrated");
        Assert.NotNull(retry.GetIndexMetadata("WrongPasswordProbe"));
    }

    /// <summary>
    /// Regression: under <see cref="BackendOverrideScope"/>(useSqlite: true)
    /// the migration's internal <c>InitializeLiteDbOnly</c> helper used
    /// to clear ONLY the env var, not the AsyncLocal override. Result:
    /// the inner Initialize re-evaluated <c>ShouldUseSqlite</c>, saw
    /// the override still pinned to true, re-detected the LiteDB file
    /// at the target path, and called <c>MigrateFromLiteDb</c> again -
    /// infinite recursion until the test host stack-overflowed and
    /// aborted the entire test run (with xUnit reporting a misleading
    /// passing aggregate count from before the crash).
    ///
    /// <para>
    /// Without the fix this test stack-overflows the test host. With
    /// the fix it migrates the seeded LiteDB into SQLite cleanly. The
    /// defence-in-depth per-instance re-entry guard in
    /// <see cref="LocalDatabaseService.Initialize"/> converts any
    /// future regression into a fast-failing
    /// <see cref="InvalidOperationException"/> instead of a stack
    /// overflow.
    /// </para>
    /// </summary>
    [Fact]
    public void Migration_DoesNotRecurseUnderAsyncLocalOverride()
    {
        // Arrange: seed LiteDB.
        using (var _flagOff = new BackendOverrideScope(useSqlite: false))
        using (var seed = new LocalDatabaseService())
        {
            seed.Initialize(_dbPath, Password.AsSpan());
            seed.SetIndexMetadata("RecursionProbe", DateTime.UtcNow);
        }

        // Act: open with the AsyncLocal override pinned to true. Migration
        // must complete WITHOUT recursing back into MigrateFromLiteDb via
        // its own InitializeLiteDbOnly helper.
        using var _flagOn = new BackendOverrideScope(useSqlite: true);
        using var migrated = new LocalDatabaseService();

        // If the bug is back this call recurses until stack overflow.
        // The defence-in-depth guard converts that into a fast-failing
        // InvalidOperationException; either way the test catches the
        // regression.
        migrated.Initialize(_dbPath, Password.AsSpan());

        // Assert: migration completed normally.
        Assert.True(File.Exists(_dbPath + ".litedb-backup"),
            "Migration should have run and renamed LiteDB to .litedb-backup");
        Assert.NotNull(migrated.GetIndexMetadata("RecursionProbe"));
    }
}
