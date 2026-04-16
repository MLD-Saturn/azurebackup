using System.Text.RegularExpressions;
using AzureBackup.Core;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 1 / P8: measures <see cref="BlobNameValidator.ValidateChunkHash"/>
/// against the previous regex-based implementation. The validator runs on
/// every chunk upload and download, so even small per-call wins add up.
/// </summary>
[MemoryDiagnoser]
public class ValidateChunkHashBenchmark
{
    // Representative SHA-256 hex string (64 chars, mixed case).
    private const string ValidHash = "A1B2C3D4E5F607182930AABBCCDDEEFF0011223344556677889900aabbccddee";

    // Pre-compiled regex matching the old implementation, so the comparison is honest.
    private static readonly Regex LegacyPattern = new(@"^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);

    [Benchmark(Baseline = true, Description = "Legacy: compiled regex")]
    public bool Legacy_Regex()
    {
        return LegacyPattern.IsMatch(ValidHash);
    }

    [Benchmark(Description = "Phase1: manual ASCII-hex scan")]
    public void Phase1_ManualScan()
    {
        BlobNameValidator.ValidateChunkHash(ValidHash);
    }
}
