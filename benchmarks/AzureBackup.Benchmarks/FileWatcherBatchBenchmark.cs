using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 4 / P5: compares per-change <see cref="LocalDatabaseService.QueueFileChange"/>
/// calls against the bulk <see cref="LocalDatabaseService.QueueFileChangesBatch"/>.
///
/// <para>
/// The file watcher and the initial-sync path used to commit one LiteDB transaction
/// per change. For bursts of thousands of changes (IDE rebuild, git checkout, first
/// folder scan) that hammered the DB lock and amplified WAL churn. The batched API
/// wraps the whole burst in a single transaction.
/// </para>
///
/// <para>
/// Each benchmark creates a fresh encrypted database under a temporary directory,
/// fills it with <see cref="ChangeCount"/> synthetic changes, and reports the
/// wall-clock time for the persist phase. The database is torn down in
/// <see cref="GlobalCleanup"/>.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class FileWatcherBatchBenchmark
{
    private const string Password = "BenchmarkPassword123!";

    [Params(100, 1_000, 10_000)]
    public int ChangeCount { get; set; }

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;
    private LocalDatabaseService _databaseService = null!;
    private FileChangeEvent[] _changes = Array.Empty<FileChangeEvent>();

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh DB per iteration so dedup / replace behaviour doesn't skew results.
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, Password);

        _changes = new FileChangeEvent[ChangeCount];
        for (int i = 0; i < ChangeCount; i++)
        {
            _changes[i] = new FileChangeEvent
            {
                FilePath = $"C:\\bench\\file-{i:D6}.txt",
                ChangeType = FileChangeType.Modified,
                DetectedAt = DateTime.UtcNow
            };
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _databaseService?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Baseline = true, Description = "Legacy: per-change QueueFileChange")]
    public void Legacy_PerChange()
    {
        foreach (var change in _changes)
        {
            _databaseService.QueueFileChange(change);
        }
    }

    [Benchmark(Description = "Phase4: QueueFileChangesBatch")]
    public void Phase4_Batch()
    {
        _databaseService.QueueFileChangesBatch(_changes);
    }
}
