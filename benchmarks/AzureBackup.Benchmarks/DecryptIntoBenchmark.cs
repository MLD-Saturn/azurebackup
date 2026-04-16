using System.Buffers;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 3 / P7: compares <c>Decrypt</c> (allocates a fresh <c>byte[]</c> per call)
/// against <c>DecryptInto</c> with a pooled destination buffer.
///
/// <para>
/// On the metadata-download hot path this runs once per backed-up file; at
/// hundreds of thousands of files the saved LOH allocations add up to significant
/// GC relief. The benchmark parametrises over plaintext sizes representative of
/// small / medium / large metadata payloads.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class DecryptIntoBenchmark
{
    private EncryptionService _encryptionService = null!;
    private byte[] _encrypted = Array.Empty<byte>();

    [Params(1_024, 65_536, 1_048_576)]
    public int PlaintextSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _encryptionService = new EncryptionService();
        var key = new byte[32];
        new Random(42).NextBytes(key);
        _encryptionService.Initialize(key);

        var plaintext = new byte[PlaintextSize];
        new Random(43).NextBytes(plaintext);
        _encrypted = _encryptionService.Encrypt(plaintext);
    }

    [Benchmark(Baseline = true, Description = "Legacy: Decrypt (allocating)")]
    public byte[] Legacy_Decrypt()
    {
        return _encryptionService.Decrypt(_encrypted);
    }

    [Benchmark(Description = "Phase3: DecryptInto (pooled buffer)")]
    public int Phase3_DecryptInto()
    {
        var plaintextMax = _encrypted.Length - EncryptionService.EncryptionOverhead;
        var rented = ArrayPool<byte>.Shared.Rent(plaintextMax);
        try
        {
            var length = _encryptionService.DecryptInto(_encrypted, rented.AsSpan(0, plaintextMax));
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
