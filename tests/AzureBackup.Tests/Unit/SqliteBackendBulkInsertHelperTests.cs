using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests the internal <c>BulkInsertFiles</c> helper on
/// <see cref="SqliteBackend"/> at miniature scale. The helper exists to
/// keep C-3 setup time reasonable; if it diverges from the canonical
/// SaveBackedUpFile path the benchmark numbers become misleading. These
/// tests assert the data shape is identical to what SaveBackedUpFile
/// would have produced.
/// </summary>
public class SqliteBackendBulkInsertHelperTests : IDisposable
{
    private readonly string _testDir;
    private readonly SqliteBackend _backend;

    public SqliteBackendBulkInsertHelperTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-bench-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _backend = new SqliteBackend();
        _backend.Initialize(Path.Combine(_testDir, "h.db"), "BenchHelperPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static BackedUpFile MakeFile(string path, params string[] chunkHashes)
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        var file = new BackedUpFile
        {
            LocalPath = path,
            BlobName = $"metadata/{Path.GetFileName(path)}.json",
            FileSize = chunkHashes.Length * 1024,
            LastModified = when,
            FileHash = "FH-" + path.GetHashCode().ToString("X8"),
            Status = BackupStatus.Completed,
            BackedUpAt = when,
            MetadataVersion = 1,
        };
        for (var i = 0; i < chunkHashes.Length; i++)
        {
            file.Chunks.Add(new ChunkInfo
            {
                Index = i,
                Offset = i * 1024L,
                Length = 1024,
                Hash = chunkHashes[i],
                BlobName = "chunks/" + chunkHashes[i],
            });
        }
        return file;
    }

    [Fact]
    public void BulkInsertFiles_RoundTripsLikeSaveBackedUpFile()
    {
        // Arrange: seed chunk_index so reverse-index reads can join.
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        foreach (var h in new[] { "AAAA", "BBBB", "CCCC", "DDDD" })
        {
            _backend.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = h, FirstUploadedAt = when,
                SizeBytes = 1024, ReferenceCount = 1, LastVerifiedAt = when,
            });
        }

        var files = new List<BackedUpFile>
        {
            MakeFile(@"C:\bulk\one.bin", "AAAA", "BBBB"),
            MakeFile(@"C:\bulk\two.bin", "CCCC"),
            MakeFile(@"C:\bulk\three.bin", "DDDD", "AAAA"),
        };

        // Act
        _backend.BulkInsertFiles(files);

        // Assert: every file readable, each chunk list correct.
        var one = _backend.GetBackedUpFile(@"C:\bulk\one.bin");
        Assert.NotNull(one);
        Assert.Equal(2, one!.Chunks.Count);
        Assert.Equal("AAAA", one.Chunks[0].Hash);
        Assert.Equal("BBBB", one.Chunks[1].Hash);

        var two = _backend.GetBackedUpFile(@"C:\bulk\two.bin");
        Assert.NotNull(two);
        Assert.Single(two!.Chunks);
        Assert.Equal("CCCC", two.Chunks[0].Hash);

        var three = _backend.GetBackedUpFile(@"C:\bulk\three.bin");
        Assert.NotNull(three);
        Assert.Equal(2, three!.Chunks.Count);

        // Reverse index populated as a side-effect.
        var oneRefs = _backend.GetChunkEntriesForFile(@"C:\bulk\one.bin");
        Assert.Equal(2, oneRefs.Count);

        var threeRefs = _backend.GetChunkEntriesForFile(@"C:\bulk\three.bin");
        Assert.Equal(2, threeRefs.Count);
    }

    [Fact]
    public void BulkInsertFiles_EmptySequence_NoOp()
    {
        _backend.BulkInsertFiles(Array.Empty<BackedUpFile>());
        Assert.Empty(_backend.GetAllBackedUpFiles());
    }

    [Fact]
    public void BulkInsertFiles_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _backend.BulkInsertFiles(null!));
    }

    [Fact]
    public void ClearReverseChunkIndexForBenchmark_RemovesRefsAndSentinel()
    {
        // Arrange: seed a file via the bulk helper (which writes
        // chunk_file_refs) and mark the reverse index built.
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        _backend.SaveChunkIndexEntry(new ChunkIndexEntry
        {
            ChunkHash = "AAAA", FirstUploadedAt = when,
            SizeBytes = 1024, ReferenceCount = 1, LastVerifiedAt = when,
        });
        _backend.BulkInsertFiles(new[] { MakeFile(@"C:\clear.bin", "AAAA") });
        _backend.SetIndexMetadata("ReverseIndexBuiltAt", DateTime.UtcNow);
        Assert.True(_backend.IsReverseChunkIndexBuilt());
        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\clear.bin"));

        // Act
        _backend.ClearReverseChunkIndexForBenchmark();

        // Assert: refs gone, sentinel cleared, file row + chunks intact.
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\clear.bin"));
        Assert.False(_backend.IsReverseChunkIndexBuilt());
        var file = _backend.GetBackedUpFile(@"C:\clear.bin");
        Assert.NotNull(file);
        Assert.Single(file!.Chunks);
    }
}
