using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1e (part 1 of 2): round-trip tests for the
/// <c>chunk_index</c> table. Covers single-row CRUD, bulk insert with
/// upsert semantics, the summary projection used by statistics, and
/// the orphan filter.
///
/// <para>
/// Reverse-index methods (<c>chunk_file_refs</c>) land in C-1e part 2 -
/// they're conceptually a separate surface and the tests group naturally
/// that way.
/// </para>
/// </summary>
public class SqliteBackendChunkIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendChunkIndexTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-cidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "cidx.db");
        _backend = new SqliteBackend();
        _backend.Initialize(_dbPath, "ChunkIdxTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static ChunkIndexEntry MakeEntry(string hash, int refCount = 1,
        StorageTier tier = StorageTier.Hot)
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        return new ChunkIndexEntry
        {
            ChunkHash = hash,
            FirstUploadedAt = when,
            OriginalUploaderPath = $@"C:\src\{hash}.bin",
            SizeBytes = 4096,
            ReferenceCount = refCount,
            CurrentTier = tier,
            LastVerifiedAt = when.AddMinutes(5),
        };
    }

    [Fact]
    public void GetChunkIndexEntry_MissingHash_ReturnsNull()
    {
        Assert.Null(_backend.GetChunkIndexEntry("DEADBEEF"));
    }

    [Fact]
    public void SaveChunkIndexEntry_RoundTripsAllFields()
    {
        var saved = MakeEntry("AAAA", refCount: 3, tier: StorageTier.Cool);
        _backend.SaveChunkIndexEntry(saved);

        var loaded = _backend.GetChunkIndexEntry("AAAA");

        Assert.NotNull(loaded);
        Assert.Equal(saved.ChunkHash, loaded!.ChunkHash);
        Assert.Equal(saved.FirstUploadedAt, loaded.FirstUploadedAt);
        Assert.Equal(saved.OriginalUploaderPath, loaded.OriginalUploaderPath);
        Assert.Equal(saved.SizeBytes, loaded.SizeBytes);
        Assert.Equal(saved.ReferenceCount, loaded.ReferenceCount);
        Assert.Equal(saved.CurrentTier, loaded.CurrentTier);
        Assert.Equal(saved.LastVerifiedAt, loaded.LastVerifiedAt);
        Assert.Empty(loaded.ReferencingFiles); // Authoritatively empty - reverse index lives elsewhere
    }

    [Fact]
    public void SaveChunkIndexEntry_DuplicateHash_Updates()
    {
        var first = MakeEntry("AAAA", refCount: 1);
        _backend.SaveChunkIndexEntry(first);

        var second = MakeEntry("AAAA", refCount: 5, tier: StorageTier.Archive);
        _backend.SaveChunkIndexEntry(second);

        var loaded = _backend.GetChunkIndexEntry("AAAA");
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.ReferenceCount);
        Assert.Equal(StorageTier.Archive, loaded.CurrentTier);
    }

    [Fact]
    public void DeleteChunkIndexEntry_RemovesRow()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("AAAA"));
        _backend.DeleteChunkIndexEntry("AAAA");

        Assert.Null(_backend.GetChunkIndexEntry("AAAA"));
    }

    [Fact]
    public void DeleteChunkIndexEntry_MissingHash_DoesNotThrow()
    {
        // LiteDB Delete on a missing row is a no-op; we match that contract.
        _backend.DeleteChunkIndexEntry("MISSING");
    }

    [Fact]
    public void BulkInsertChunkIndexEntries_InsertsAllRows()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => MakeEntry($"HASH{i:D4}", refCount: i % 5))
            .ToList();

        _backend.BulkInsertChunkIndexEntries(entries);

        Assert.Equal(100, _backend.GetChunkIndexCount());
        Assert.Equal(3, _backend.GetChunkIndexEntry("HASH0003")!.ReferenceCount);
    }

    [Fact]
    public void BulkInsertChunkIndexEntries_OverlappingHashes_Upsert()
    {
        // Existing row.
        _backend.SaveChunkIndexEntry(MakeEntry("AAAA", refCount: 1));

        // Bulk insert overlaps that row plus a fresh one.
        _backend.BulkInsertChunkIndexEntries(new[]
        {
            MakeEntry("AAAA", refCount: 99),
            MakeEntry("BBBB", refCount: 7),
        });

        Assert.Equal(99, _backend.GetChunkIndexEntry("AAAA")!.ReferenceCount);
        Assert.Equal(7, _backend.GetChunkIndexEntry("BBBB")!.ReferenceCount);
        Assert.Equal(2, _backend.GetChunkIndexCount());
    }

    [Fact]
    public void BulkInsertChunkIndexEntries_EmptySequence_NoOp()
    {
        _backend.BulkInsertChunkIndexEntries(Array.Empty<ChunkIndexEntry>());
        Assert.Equal(0, _backend.GetChunkIndexCount());
    }

    [Fact]
    public void GetAllChunkIndexEntries_ReturnsEverything()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("AAAA"));
        _backend.SaveChunkIndexEntry(MakeEntry("BBBB"));
        _backend.SaveChunkIndexEntry(MakeEntry("CCCC"));

        var all = _backend.GetAllChunkIndexEntries();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, e => e.ChunkHash == "AAAA");
        Assert.Contains(all, e => e.ChunkHash == "BBBB");
        Assert.Contains(all, e => e.ChunkHash == "CCCC");
    }

    [Fact]
    public void GetChunkIndexSummaryMap_ProjectsOnlyHotFields()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("AAAA", refCount: 2, tier: StorageTier.Hot));
        _backend.SaveChunkIndexEntry(MakeEntry("BBBB", refCount: 0, tier: StorageTier.Cold));

        var summary = _backend.GetChunkIndexSummaryMap();

        Assert.Equal(2, summary.Count);
        Assert.Equal((2, 4096L, StorageTier.Hot), summary["AAAA"]);
        Assert.Equal((0, 4096L, StorageTier.Cold), summary["BBBB"]);
    }

    [Fact]
    public void GetOrphanedChunks_ReturnsOnlyZeroRefcountRows()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("ALIVE", refCount: 1));
        _backend.SaveChunkIndexEntry(MakeEntry("ORPHAN1", refCount: 0));
        _backend.SaveChunkIndexEntry(MakeEntry("ORPHAN2", refCount: 0));

        var orphans = _backend.GetOrphanedChunks();

        Assert.Equal(2, orphans.Count);
        Assert.All(orphans, e => Assert.Equal(0, e.ReferenceCount));
        Assert.DoesNotContain(orphans, e => e.ChunkHash == "ALIVE");
    }

    [Fact]
    public void ClearChunkIndex_RemovesEverything()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("AAAA"));
        _backend.SaveChunkIndexEntry(MakeEntry("BBBB"));

        _backend.ClearChunkIndex();

        Assert.Equal(0, _backend.GetChunkIndexCount());
    }

    [Fact]
    public void SaveChunkIndexEntry_SurvivesReopen()
    {
        _backend.SaveChunkIndexEntry(MakeEntry("PERSIST", refCount: 42));
        _backend.Dispose();

        using var reopened = new SqliteBackend();
        reopened.Initialize(_dbPath, "ChunkIdxTestPwd!".AsSpan());

        var loaded = reopened.GetChunkIndexEntry("PERSIST");
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded!.ReferenceCount);
    }

    [Fact]
    public void GetChunkIndexEntry_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => _backend.GetChunkIndexEntry(""));
        Assert.Throws<ArgumentException>(() => _backend.GetChunkIndexEntry("   "));
    }

    [Fact]
    public void SaveChunkIndexEntry_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.SaveChunkIndexEntry(null!));
    }

    [Fact]
    public void BulkInsertChunkIndexEntries_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.BulkInsertChunkIndexEntries(null!));
    }
}
