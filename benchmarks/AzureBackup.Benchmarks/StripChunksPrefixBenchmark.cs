using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 1 / P11: measures the difference between <c>string.Replace("chunks/", "")</c>
/// and an indexed slice <c>name["chunks/".Length..]</c> when stripping the known
/// prefix from a chunk blob name. The primary motivation is correctness (Replace
/// strips every occurrence, not just the prefix), but the slice is also slightly
/// faster and allocates less on the listing hot path.
/// </summary>
[MemoryDiagnoser]
public class StripChunksPrefixBenchmark
{
    private const string Prefix = "chunks/";
    private const string BlobName = "chunks/a1b2c3d4e5f607182930aabbccddeeff0011223344556677889900aabbccddee";

    [Benchmark(Baseline = true, Description = "Legacy: string.Replace")]
    public string Legacy_Replace()
    {
        return BlobName.Replace(Prefix, "");
    }

    [Benchmark(Description = "Phase1: indexed slice")]
    public string Phase1_IndexedSlice()
    {
        return BlobName[Prefix.Length..];
    }
}
