using System.Buffers;
using System.Threading.Channels;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 6.1 / discovered-#5: characterises the streaming CDC implementation
/// across file sizes and dedup ratios.
///
/// <para>
/// The previous (Phase 6) loop rented a single scan buffer sized to
/// <c>MaxChunkSize + WindowSize</c> - up to <b>128 MB</b> for the giant-file
/// configuration - and re-seeked back to <c>chunkStart</c> after every chunk
/// boundary, re-reading the bytes after the boundary on the next iteration.
/// </para>
/// <para>
/// Phase 6.1 replaces this with a fixed 64 KB scratch buffer that streams the
/// file once. Per-chunk payload buffers (sized to <c>MaxChunkSize</c>) are
/// rented as needed and handed straight to the channel consumer. The scan
/// buffer reuse problem disappears entirely.
/// </para>
/// <para>
/// The expected wins are: (1) much smaller peak working set on giant-chunk
/// configs (no 128 MB rental), (2) zero re-read of bytes after a boundary,
/// (3) simpler ownership story (the payload buffer goes straight to the
/// consumer with no intermediate copy).
/// </para>
/// </summary>
[MemoryDiagnoser]
public class CdcStreamingBufferBenchmark
{
    /// <summary>
    /// File size in megabytes. 4 MB exercises the document-default config;
    /// 64 MB and 256 MB exercise the large-file config that historically
    /// rented the biggest scan buffer.
    /// </summary>
    [Params(4, 64, 256)]
    public int FileSizeMB { get; set; }

    /// <summary>
    /// Fraction of chunks already deduplicated. The streaming path returns
    /// the per-chunk payload buffer to the pool immediately on dedup so the
    /// allocation curve is dominated by the non-dedup fraction.
    /// </summary>
    [Params(0.0, 1.0)]
    public double DedupRatio { get; set; }

    private string _testDir = string.Empty;
    private string _filePath = string.Empty;
    private HashSet<string> _existingHashes = new(StringComparer.Ordinal);
    private readonly ChunkingService _chunkingService = new();

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-streambench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _filePath = Path.Combine(_testDir, $"bench-{FileSizeMB}mb.bin");

        var bytes = new byte[FileSizeMB * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(_filePath, bytes);

        // Warm hashes once so DedupRatio=1.0 reflects only the streaming cost
        // of skipping dispatched payloads, not the cost of discovering hashes.
        var (chunks, _) = await ChunkAndDrainAsync([]);
        var dedupCount = (int)(chunks.Count * DedupRatio);
        _existingHashes = chunks
            .Take(dedupCount)
            .Select(c => c.Hash)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "Phase6.1: streaming scratch + per-chunk pool")]
    public async Task<int> StreamingChunking()
    {
        var (chunks, _) = await ChunkAndDrainAsync(_existingHashes);
        return chunks.Count;
    }

    private async Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkAndDrainAsync(
        HashSet<string> existingHashes)
    {
        var channel = Channel.CreateUnbounded<ChunkPayload>();
        var result = await _chunkingService.ChunkAndStreamChangedAsync(
            _filePath, existingHashes, channel.Writer, cdcProgress: null);
        channel.Writer.Complete();

        await foreach (var payload in channel.Reader.ReadAllAsync())
        {
            ArrayPool<byte>.Shared.Return(payload.Data);
        }
        return result;
    }
}
