using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 6 / P1: compares the new single-pass
/// <see cref="ChunkingService.ChunkAndStreamChangedAsync"/> against a synthetic
/// two-pass baseline that mirrors the previous implementation's I/O cost
/// (CDC scan, then re-read every non-deduplicated chunk from the same stream).
///
/// <para>
/// The old code is no longer in tree, so the baseline simulates its work by
/// running the new method to completion (which gives the chunk metadata) and
/// then performing a second sequential file open + per-chunk seek + read +
/// hash that would have been done by the old Phase 2 pass. This isolates the
/// I/O cost difference; CPU work (rolling hash + chunk hash) is identical
/// between the two paths and need not be re-measured.
/// </para>
///
/// <para>
/// Parameterised by file size and the deduplication ratio - i.e. the
/// fraction of chunks that are already in <c>existingHashes</c> and would
/// have been skipped by Phase 2 anyway. At 100% dedup the two paths converge
/// (Phase 2 had nothing to do); at 0% dedup the I/O-saving win is largest.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class CdcSinglePassBenchmark
{
    /// <summary>
    /// File size in megabytes. 4 MB and 64 MB span the typical document and
    /// large-media-file working sets.
    /// </summary>
    [Params(4, 64)]
    public int FileSizeMB { get; set; }

    /// <summary>
    /// Fraction of chunks already deduplicated (0.0 = all new, 1.0 = all
    /// already present). The two-pass cost penalty grows with the
    /// non-deduplicated fraction.
    /// </summary>
    [Params(0.0, 0.5, 1.0)]
    public double DedupRatio { get; set; }

    private string _testDir = string.Empty;
    private string _filePath = string.Empty;
    private HashSet<string> _existingHashes = new(StringComparer.Ordinal);
    private readonly ChunkingService _chunkingService = new();

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-cdcbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _filePath = Path.Combine(_testDir, $"bench-{FileSizeMB}mb.bin");

        var bytes = new byte[FileSizeMB * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(_filePath, bytes);

        // Discover the chunk hashes once so both benchmark methods share an
        // identical existingHashes set sized to DedupRatio of the chunks.
        var (chunks, _) = await ChunkAndDrainAsync(existingHashes: []);
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

    /// <summary>
    /// Phase 6 single-pass: CDC scan and changed-chunk dispatch in one pass.
    /// </summary>
    [Benchmark(Description = "Phase6: single-pass CDC + dispatch")]
    public async Task<int> SinglePass()
    {
        var (chunks, _) = await ChunkAndDrainAsync(_existingHashes);
        return chunks.Count;
    }

    /// <summary>
    /// Pre-Phase-6 baseline simulation: single-pass CDC followed by a second
    /// sequential pass that re-reads + re-hashes every non-deduplicated chunk
    /// (the work the removed Phase 2 used to do). The first call's dispatched
    /// payloads are returned to the pool immediately so they do not skew
    /// allocation numbers.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Legacy: two-pass CDC + re-read")]
    public async Task<int> TwoPassSimulated()
    {
        var (chunks, _) = await ChunkAndDrainAsync(_existingHashes);

        // Simulate the removed Phase 2 re-read pass.
        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);

        foreach (var chunk in chunks)
        {
            if (_existingHashes.Contains(chunk.Hash)) continue;

            var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
            try
            {
                stream.Position = chunk.Offset;
                await stream.ReadExactlyAsync(buffer.AsMemory(0, chunk.Length));
                _ = SHA256.HashData(buffer.AsSpan(0, chunk.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

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
