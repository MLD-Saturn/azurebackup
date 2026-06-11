using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// W6 Phase 3 (Item 2): correctness regression guard for the two-lane backup
/// dispatch in <c>BackupOrchestrator.BackupFilesCoreAsync</c>.
///
/// <para>
/// Pre-W6 the core ran a single <c>Parallel.ForEachAsync</c> whose degree was
/// the budget-clamped <c>ComputeEffectiveFileConcurrency</c> -- sized for the
/// 512 MB worst-case LARGE file -- so a small <c>MemoryLimitMB</c> serialised
/// EVERY file (including kilobyte files) to 1-2 at a time. W6 splits the batch
/// into a high-concurrency small-file lane (&lt;= <c>RestoreService.SmallFileThresholdBytes</c>)
/// and the budget-clamped large-file lane, both sharing the same
/// <c>MemoryBudget</c>. The throughput improvement itself is a benchmark
/// concern; these tests pin the property that MUST hold regardless of timing:
/// every file in a mixed small+large batch is still backed up exactly once and
/// marked Completed, even when the budget clamps the large lane to a single
/// file.
/// </para>
/// </summary>
public class BackupOrchestratorSmallFileLaneTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _dbPath = null!;

    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator _orchestrator = null!;

    private const string TestPassword = "SmallFileLaneTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"SmallFileLaneTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _dbPath = Path.Combine(_testDirectory, "test.db");

        Directory.CreateDirectory(_sourceDirectory);

        _encryptionService = new EncryptionService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        _fileWatcherService = new FileWatcherService(_databaseService);
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            _blobService,
            _fileWatcherService);

        await _blobService.ConnectAsync("fake-connection-string", "test-container");
    }

    public async Task DisposeAsync()
    {
        try { await _orchestrator.DisposeAsync(); } catch { }
        try { _encryptionService.Dispose(); } catch { }
        try { _databaseService.Dispose(); } catch { }

        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task BackupFilesAsync_MixedSmallAndLargeUnderConstrainedBudget_AllFilesCompleted()
    {
        await _orchestrator.InitializeAsync(TestPassword);

        // Constrain the budget so the LARGE lane clamps to a single file
        // (ComputeEffectiveFileConcurrency = 512 MB / 512 MB-per-file = 1).
        // Pre-W6 this same clamp throttled the small files too; the two-lane
        // split must let the small files run on their own lane.
        var config = _databaseService.GetConfiguration();
        config.MemoryLimitEnabled = true;
        config.MemoryLimitMB = 512;
        _databaseService.SaveConfiguration(config);

        var smallPaths = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            var p = Path.Combine(_sourceDirectory, $"small_{i}.bin");
            await File.WriteAllBytesAsync(p, RandomNumberGenerator.GetBytes(8 * 1024));
            smallPaths.Add(p);
        }

        var largePaths = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            // 18 MB is just over RestoreService.SmallFileThresholdBytes (16 MB),
            // so these route through the budget-clamped large lane.
            var p = Path.Combine(_sourceDirectory, $"large_{i}.bin");
            await File.WriteAllBytesAsync(p, RandomNumberGenerator.GetBytes(18 * 1024 * 1024));
            largePaths.Add(p);
        }

        var allPaths = smallPaths.Concat(largePaths).ToList();

        await _orchestrator.BackupFilesAsync(allPaths);

        foreach (var p in allPaths)
        {
            var record = _databaseService.GetBackedUpFile(p);
            Assert.NotNull(record);
            Assert.Equal(BackupStatus.Completed, record!.Status);
            Assert.NotEmpty(record.Chunks);
        }
    }

    [Fact]
    public async Task BackupFilesAsync_OnlySmallFilesUnderConstrainedBudget_AllFilesCompleted()
    {
        await _orchestrator.InitializeAsync(TestPassword);

        var config = _databaseService.GetConfiguration();
        config.MemoryLimitEnabled = true;
        config.MemoryLimitMB = 512;
        _databaseService.SaveConfiguration(config);

        // All-small batch exercises the small lane with an EMPTY large lane
        // (the largeFiles.Count == 0 branch).
        var paths = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            var p = Path.Combine(_sourceDirectory, $"tiny_{i}.bin");
            await File.WriteAllBytesAsync(p, RandomNumberGenerator.GetBytes(4 * 1024));
            paths.Add(p);
        }

        await _orchestrator.BackupFilesAsync(paths);

        foreach (var p in paths)
        {
            var record = _databaseService.GetBackedUpFile(p);
            Assert.NotNull(record);
            Assert.Equal(BackupStatus.Completed, record!.Status);
        }
    }
}
