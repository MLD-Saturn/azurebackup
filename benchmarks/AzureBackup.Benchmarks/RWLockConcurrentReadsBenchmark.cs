using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 5 / P2: demonstrates the concurrency win of the reader/writer lock
/// over the previous coarse monitor. Runs N parallel <c>GetAllBackedUpFiles</c>
/// readers and reports total wall-clock time; under the monitor every reader
/// serialised, under the RWLock they run in parallel.
/// </summary>
[MemoryDiagnoser]
public class RWLockConcurrentReadsBenchmark
{
    private const string Password = "BenchmarkPassword123!";

    [Params(1, 4, 16, 32)]
    public int Readers { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LocalDatabaseService _databaseService = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, Password);

        // Populate with 2 000 files so each GetAllBackedUpFiles does real work.
        for (int i = 0; i < 2_000; i++)
        {
            _databaseService.SaveBackedUpFile(new BackedUpFile
            {
                LocalPath = $"C:\\bench\\f-{i:D6}.bin",
                FileSize = 1024,
                LastModified = DateTime.UtcNow,
                FileHash = Guid.NewGuid().ToString("N"),
                Status = BackupStatus.Completed,
                BackedUpAt = DateTime.UtcNow
            });
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "Phase5: N parallel readers under RWLock")]
    public int ParallelReaders()
    {
        var tasks = new Task<int>[Readers];
        for (int i = 0; i < Readers; i++)
        {
            tasks[i] = Task.Run(() => _databaseService.GetAllBackedUpFiles().Count);
        }
        Task.WaitAll(tasks);
        return tasks[0].Result;
    }
}
