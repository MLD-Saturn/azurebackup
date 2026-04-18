using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1g: round-trip tests for <c>GetStatistics</c> on the
/// SQLite backend. Validates that the single-pass aggregate matches
/// what hand-counting the underlying tables would produce.
///
/// <para>
/// Mirrors the assertions that the LiteDB-era LocalDatabaseService
/// produced via <c>FindAll().ToList()</c> + LINQ, but here the work is
/// done in three indexed queries with no intermediate object allocation.
/// </para>
/// </summary>
public class SqliteBackendStatisticsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendStatisticsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "stats.db");
        _backend = new SqliteBackend();
        _backend.Initialize(_dbPath, "StatsTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static BackedUpFile MakeFile(string path, BackupStatus status, long size = 1024)
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        return new BackedUpFile
        {
            LocalPath = path,
            BlobName = $"metadata/{Path.GetFileName(path)}.json",
            FileSize = size,
            LastModified = when,
            FileHash = "H-" + path.GetHashCode().ToString("X8"),
            Status = status,
            BackedUpAt = when,
            MetadataVersion = 1,
        };
    }

    [Fact]
    public void GetStatistics_FreshDb_ReturnsAllZeros()
    {
        var stats = _backend.GetStatistics();

        Assert.Equal(0, stats.TotalFiles);
        Assert.Equal(0, stats.TotalSize);
        Assert.Equal(0, stats.CompletedFiles);
        Assert.Equal(0, stats.PendingFiles);
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(0, stats.PendingChanges);
        Assert.Null(stats.LastBackupTime);
        Assert.Equal(0, stats.TotalBytesUploaded);
    }

    [Fact]
    public void GetStatistics_FilesByStatus_CountsCorrectly()
    {
        // Arrange: 2 completed, 1 pending, 1 failed, 1 excluded (not counted).
        _backend.SaveBackedUpFile(MakeFile(@"C:\done1.txt", BackupStatus.Completed, 1000));
        _backend.SaveBackedUpFile(MakeFile(@"C:\done2.txt", BackupStatus.Completed, 2000));
        _backend.SaveBackedUpFile(MakeFile(@"C:\pend.txt",  BackupStatus.Pending,   3000));
        _backend.SaveBackedUpFile(MakeFile(@"C:\fail.txt",  BackupStatus.Failed,    4000));
        _backend.SaveBackedUpFile(MakeFile(@"C:\excl.txt",  BackupStatus.Excluded,  5000));

        // Act
        var stats = _backend.GetStatistics();

        // Assert
        Assert.Equal(5, stats.TotalFiles);
        Assert.Equal(15_000, stats.TotalSize);
        Assert.Equal(2, stats.CompletedFiles);
        Assert.Equal(1, stats.PendingFiles);
        Assert.Equal(1, stats.FailedFiles);
        // Excluded is intentionally not in any breakdown bucket - matches the
        // LiteDB-era contract where only Completed/Pending/Failed are tracked.
    }

    [Fact]
    public void GetStatistics_PendingChangesCount_ReflectsQueue()
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        _backend.QueueFileChange(new FileChangeEvent { FilePath = @"C:\a.txt", DetectedAt = when });
        _backend.QueueFileChange(new FileChangeEvent { FilePath = @"C:\b.txt", DetectedAt = when });
        _backend.QueueFileChange(new FileChangeEvent { FilePath = @"C:\c.txt", DetectedAt = when });

        Assert.Equal(3, _backend.GetStatistics().PendingChanges);

        _backend.RemovePendingChange(@"C:\a.txt");
        Assert.Equal(2, _backend.GetStatistics().PendingChanges);
    }

    [Fact]
    public void GetStatistics_PendingChanges_RequeueIsNotDoubleCounted()
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        _backend.QueueFileChange(new FileChangeEvent { FilePath = @"C:\dup.txt", DetectedAt = when });
        // Re-queueing the same path replaces the previous row (C-1f contract).
        _backend.QueueFileChange(new FileChangeEvent { FilePath = @"C:\dup.txt", DetectedAt = when });

        Assert.Equal(1, _backend.GetStatistics().PendingChanges);
    }

    [Fact]
    public void GetStatistics_ConfigFields_ReadDirectlyFromConfigRow()
    {
        // Arrange
        var lastBackup = new DateTime(2026, 4, 17, 22, 15, 30, DateTimeKind.Utc);
        _backend.SaveConfiguration(new BackupConfiguration
        {
            LastBackupTime = lastBackup,
            TotalBytesUploaded = 1_234_567_890,
        });

        // Act
        var stats = _backend.GetStatistics();

        // Assert
        Assert.Equal(lastBackup, stats.LastBackupTime);
        Assert.Equal(DateTimeKind.Utc, stats.LastBackupTime!.Value.Kind);
        Assert.Equal(1_234_567_890, stats.TotalBytesUploaded);
    }

    [Fact]
    public void GetStatistics_OnlyExcludedFiles_TotalSizeStillSummed()
    {
        // Edge case: TotalSize sums every file regardless of status; the
        // breakdown buckets are independent.
        _backend.SaveBackedUpFile(MakeFile(@"C:\excl1.txt", BackupStatus.Excluded, 100));
        _backend.SaveBackedUpFile(MakeFile(@"C:\excl2.txt", BackupStatus.Excluded, 200));

        var stats = _backend.GetStatistics();

        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(300, stats.TotalSize);
        Assert.Equal(0, stats.CompletedFiles);
        Assert.Equal(0, stats.PendingFiles);
        Assert.Equal(0, stats.FailedFiles);
    }

    [Fact]
    public void GetStatistics_AfterFileDelete_ReflectsRemoval()
    {
        // No public DeleteBackedUpFile yet (it's not on the backend interface),
        // but re-saving with a different status flips the counts.
        _backend.SaveBackedUpFile(MakeFile(@"C:\flip.txt", BackupStatus.Failed, 1000));
        Assert.Equal(1, _backend.GetStatistics().FailedFiles);

        _backend.SaveBackedUpFile(MakeFile(@"C:\flip.txt", BackupStatus.Completed, 1000));
        var stats = _backend.GetStatistics();
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(1, stats.CompletedFiles);
        Assert.Equal(1, stats.TotalFiles); // upsert didn't double-count
    }

    [Fact]
    public void GetStatistics_LargeFileSizes_NoOverflow()
    {
        // SUM is INTEGER (64-bit) in SQLite; verify big sums still fit.
        _backend.SaveBackedUpFile(MakeFile(@"C:\huge1.bin", BackupStatus.Completed, 5_000_000_000L));
        _backend.SaveBackedUpFile(MakeFile(@"C:\huge2.bin", BackupStatus.Completed, 5_000_000_000L));

        Assert.Equal(10_000_000_000L, _backend.GetStatistics().TotalSize);
    }

    [Fact]
    public void GetStatistics_SurvivesReopen()
    {
        _backend.SaveBackedUpFile(MakeFile(@"C:\persist.txt", BackupStatus.Completed, 4096));
        _backend.SaveConfiguration(new BackupConfiguration { TotalBytesUploaded = 42 });
        _backend.Dispose();

        using var reopened = new SqliteBackend();
        reopened.Initialize(_dbPath, "StatsTestPwd!".AsSpan());

        var stats = reopened.GetStatistics();
        Assert.Equal(1, stats.TotalFiles);
        Assert.Equal(4096, stats.TotalSize);
        Assert.Equal(42, stats.TotalBytesUploaded);
    }
}
