using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using AzureBackup.Tests.Infrastructure;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Phase 6 / P1: verifies the single-pass <c>ChunkAndStreamChangedAsync</c>
/// implementation preserves the invariants the previous two-pass code held.
///
/// <para>
/// Phase 6 collapsed the producer's CDC scan and the seek-and-upload pass
/// into one sequential pass. These tests pin down the contract callers rely on:
/// </para>
/// <list type="bullet">
///   <item>The full chunk metadata (offset, length, hash, index) is returned.</item>
///   <item>The file-level SHA-256 matches a fresh hash of the source file.</item>
///   <item>Only chunks whose hash is NOT already present in
///     <c>existingHashes</c> are dispatched to the channel; deduplicated
///     chunks have <c>BlobName</c> set in the metadata but skip the channel.</item>
///   <item>Dispatched payloads cover the chunk's exact byte range -
///     length matches the metadata and the bytes hash to the recorded hash.</item>
///   <item>Payloads arrive in increasing chunk-index order (single-pass dispatch
///     guarantees ordering; consumers may parallelise after the channel).</item>
/// </list>
/// </summary>
public class ChunkAndStreamChangedSinglePassTests : IDisposable
{
    private readonly string _testDir;
    private readonly ChunkingService _chunkingService = new();

    public ChunkAndStreamChangedSinglePassTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-cdc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SinglePass_NoExistingHashes_DispatchesEveryChunk()
    {
        // Arrange: a 4 MB file with random bytes guarantees multiple CDC chunks.
        var filePath = await CreateRandomFileAsync("payload.bin", sizeBytes: 4 * 1024 * 1024);
        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, fileHash) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes: [], channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        var dispatched = await DrainAsync(channel.Reader);

        // Assert: every produced chunk was also dispatched.
        Assert.Equal(chunks.Count, dispatched.Count);

        // Assert: ordering preserved.
        for (var i = 0; i < dispatched.Count; i++)
        {
            Assert.Equal(i, dispatched[i].Info.Index);
        }

        // Assert: file hash matches a fresh SHA-256 of the source.
        var expectedFileHash = await HashHelper.ComputeFileHashAsync(filePath);
        Assert.Equal(expectedFileHash, fileHash);

        // Assert: dispatched payload bytes match recorded chunk hashes.
        foreach (var payload in dispatched)
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(payload.Data.AsSpan(0, payload.Length)));
            Assert.Equal(payload.Info.Hash, actualHash);
        }

        ReturnAll(dispatched);
    }

    [Fact]
    public async Task SinglePass_ChunksFullyCoverFile_NoGapsOrOverlaps()
    {
        // Arrange
        var filePath = await CreateRandomFileAsync("coverage.bin", sizeBytes: 2 * 1024 * 1024);
        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, _) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes: [], channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        ReturnAll(await DrainAsync(channel.Reader));

        // Assert: chunks tile the file with no gaps or overlaps.
        var expectedOffset = 0L;
        foreach (var chunk in chunks)
        {
            Assert.Equal(expectedOffset, chunk.Offset);
            expectedOffset += chunk.Length;
        }
        Assert.Equal(new FileInfo(filePath).Length, expectedOffset);
    }

    [Fact]
    public async Task SinglePass_ExistingChunkSkipsDispatchButRecordsMetadata()
    {
        // Arrange: chunk the file once to learn its hashes.
        var filePath = await CreateRandomFileAsync("dedup.bin", sizeBytes: 2 * 1024 * 1024);
        var (firstChunks, _) = await _chunkingService.ChunkFileForTestAsync(filePath);

        // Mark the FIRST chunk as already existing.
        var existingHashes = new HashSet<string>(StringComparer.Ordinal) { firstChunks[0].Hash };

        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, _) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes, channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        var dispatched = await DrainAsync(channel.Reader);

        // Assert: same number of chunks discovered.
        Assert.Equal(firstChunks.Count, chunks.Count);

        // Assert: the first chunk is NOT in the dispatched set; all others are.
        Assert.Equal(chunks.Count - 1, dispatched.Count);
        Assert.DoesNotContain(dispatched, p => p.Info.Hash == firstChunks[0].Hash);

        // Assert: the deduplicated chunk's metadata still got a BlobName so the
        // file-metadata writer can reference it.
        var skipped = chunks.Single(c => c.Hash == firstChunks[0].Hash);
        Assert.Equal($"chunks/{firstChunks[0].Hash}", skipped.BlobName);

        ReturnAll(dispatched);
    }

    [Fact]
    public async Task SinglePass_AllChunksAlreadyExist_DispatchesNothing()
    {
        // Arrange: pre-discover every hash.
        var filePath = await CreateRandomFileAsync("alldedup.bin", sizeBytes: 1 * 1024 * 1024);
        var (priorChunks, _) = await _chunkingService.ChunkFileForTestAsync(filePath);
        var existingHashes = priorChunks.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);

        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, _) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes, channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        var dispatched = await DrainAsync(channel.Reader);

        // Assert
        Assert.Equal(priorChunks.Count, chunks.Count);
        Assert.Empty(dispatched);
        Assert.All(chunks, c => Assert.Equal($"chunks/{c.Hash}", c.BlobName));
    }

    [Fact]
    public async Task SinglePass_SmallFileBelowMinChunkSize_ProducesSingleChunk()
    {
        // Arrange: 1 KB file is well below the smallest configured min-chunk-size.
        var filePath = await CreateRandomFileAsync("tiny.bin", sizeBytes: 1024);
        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, fileHash) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes: [], channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        var dispatched = await DrainAsync(channel.Reader);

        // Assert
        var single = Assert.Single(chunks);
        Assert.Equal(0, single.Index);
        Assert.Equal(0, single.Offset);
        Assert.Equal(1024, single.Length);

        var dispatchedSingle = Assert.Single(dispatched);
        Assert.Equal(single.Hash, dispatchedSingle.Info.Hash);

        var expectedFileHash = await HashHelper.ComputeFileHashAsync(filePath);
        Assert.Equal(expectedFileHash, fileHash);

        ReturnAll(dispatched);
    }

    [Fact]
    public async Task SinglePass_EmptyFile_ProducesSingleZeroLengthChunk()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "empty.bin");
        await File.WriteAllBytesAsync(filePath, []);
        var channel = Channel.CreateUnbounded<ChunkPayload>();

        // Act
        var (chunks, fileHash) = await _chunkingService.ChunkAndStreamChangedAsync(
            filePath, existingHashes: [], channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        var dispatched = await DrainAsync(channel.Reader);

        // Assert
        var single = Assert.Single(chunks);
        Assert.Equal(0, single.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), fileHash);

        // Dispatched count matches: an empty chunk's hash equals the empty-bytes
        // SHA-256, which is not in existingHashes, so it IS dispatched.
        var dispatchedSingle = Assert.Single(dispatched);
        Assert.Equal(0, dispatchedSingle.Length);

        ReturnAll(dispatched);
    }

    private async Task<string> CreateRandomFileAsync(string name, int sizeBytes)
    {
        var path = Path.Combine(_testDir, name);
        var bytes = new byte[sizeBytes];
        // Deterministic-but-random-looking content so tests are reproducible.
        new Random(name.GetHashCode()).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task<List<ChunkPayload>> DrainAsync(ChannelReader<ChunkPayload> reader)
    {
        var payloads = new List<ChunkPayload>();
        await foreach (var p in reader.ReadAllAsync())
        {
            payloads.Add(p);
        }
        return payloads;
    }

    private static void ReturnAll(List<ChunkPayload> payloads)
    {
        foreach (var p in payloads)
        {
            ArrayPool<byte>.Shared.Return(p.Data);
        }
    }
}
