using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 1 / P9: isolates the CDC rolling-hash inner loop and compares the
/// modulo-based wrap against the branching wrap. WindowSize is held constant
/// at 48 to match production (changing it would alter chunk boundaries).
///
/// The rolling hash in <c>ChunkingService</c> runs for every byte of every
/// backed-up file on first-time backup, so this loop dominates CPU during CDC.
/// </summary>
[MemoryDiagnoser]
public class CdcRollingHashBenchmark
{
    // Must match ChunkingService.WindowSize.
    private const int WindowSize = 48;
    private const uint HashPrime = 31;
    private const uint HashPrimePower = 969_581_023; // 31^47 mod 2^32

    // 4 MB synthetic buffer — representative of an in-flight CDC read.
    private byte[] _buffer = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[4 * 1024 * 1024];
        new Random(42).NextBytes(_buffer);
    }

    /// <summary>
    /// Baseline: the original implementation using <c>% WindowSize</c>.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Legacy: modulo wrap")]
    public uint Legacy_ModuloWrap()
    {
        uint rollingHash = 0;
        var window = new byte[WindowSize];
        var windowPos = 0;
        var windowFilled = false;

        for (var i = 0; i < _buffer.Length; i++)
        {
            var b = _buffer[i];

            if (windowFilled)
            {
                var outByte = window[windowPos];
                rollingHash = (rollingHash - outByte * HashPrimePower) * HashPrime + b;
            }
            else
            {
                rollingHash = rollingHash * HashPrime + b;
            }

            window[windowPos] = b;
            windowPos = (windowPos + 1) % WindowSize;
            if (windowPos == 0) windowFilled = true;
        }

        return rollingHash;
    }

    /// <summary>
    /// Phase 1 / P9: branching wrap. Identical behaviour, no modulo.
    /// </summary>
    [Benchmark(Description = "Phase1: branching wrap")]
    public uint Phase1_BranchingWrap()
    {
        uint rollingHash = 0;
        var window = new byte[WindowSize];
        var windowPos = 0;
        var windowFilled = false;

        for (var i = 0; i < _buffer.Length; i++)
        {
            var b = _buffer[i];

            if (windowFilled)
            {
                var outByte = window[windowPos];
                rollingHash = (rollingHash - outByte * HashPrimePower) * HashPrime + b;
            }
            else
            {
                rollingHash = rollingHash * HashPrime + b;
            }

            window[windowPos] = b;
            if (++windowPos == WindowSize)
            {
                windowPos = 0;
                windowFilled = true;
            }
        }

        return rollingHash;
    }
}
