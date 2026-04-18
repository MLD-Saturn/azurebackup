using System.Runtime.InteropServices;
using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 6 / discovered-#2: demonstrates the throughput improvement from
/// padding hot atomic counters so they each occupy a full cache line.
///
/// <para>
/// The unpadded baseline declares two adjacent <c>long</c> fields in a
/// struct - exactly the layout the JIT picks for the Phase 5 stack-local
/// counters in the upload pipeline before Phase 6's fix. The padded
/// variant uses <see cref="PaddedLong"/> so each counter sits in its own
/// 64-byte cache line.
/// </para>
///
/// <para>
/// Both variants run N parallel <see cref="System.Threading.Tasks.Task"/>s
/// that each call <c>Interlocked.Add</c> in a tight loop. With unpadded
/// fields the cores invalidate each other's L1 line on every increment
/// (cache-line ping-pong); with padding the cores own disjoint lines and
/// scale linearly until they hit memory bandwidth.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class PaddedCounterBenchmark
{
    private const int IncrementsPerThread = 5_000_000;

    [Params(2, 4, 8)]
    public int Threads { get; set; }

    [Benchmark(Description = "Phase6: PaddedLong (each counter on its own cache line)")]
    public long Padded()
    {
        var a = default(PaddedLong);
        var b = default(PaddedLong);

        var tasks = new Task[Threads];
        for (var i = 0; i < Threads; i++)
        {
            // Half the threads hammer counter A, half hammer counter B - the
            // contention pattern Phase 6 actually faces, where multiple
            // consumer tasks all push to bytesUploaded and chunksUploadedCount.
            var hitsA = (i & 1) == 0;
            tasks[i] = Task.Run(() =>
            {
                for (var k = 0; k < IncrementsPerThread; k++)
                {
                    if (hitsA) a.Add(1);
                    else b.Add(1);
                }
            });
        }
        Task.WaitAll(tasks);
        return a.Read() + b.Read();
    }

    [Benchmark(Baseline = true, Description = "Legacy: adjacent longs (false-shared cache line)")]
    public long Unpadded()
    {
        var box = new TwoLongs();

        var tasks = new Task[Threads];
        for (var i = 0; i < Threads; i++)
        {
            var hitsA = (i & 1) == 0;
            tasks[i] = Task.Run(() =>
            {
                for (var k = 0; k < IncrementsPerThread; k++)
                {
                    if (hitsA) System.Threading.Interlocked.Increment(ref box.A);
                    else System.Threading.Interlocked.Increment(ref box.B);
                }
            });
        }
        Task.WaitAll(tasks);
        return box.A + box.B;
    }

    /// <summary>
    /// Two longs laid out adjacently. The CLR places them on the same
    /// 64-byte cache line so concurrent updates ping-pong the line between
    /// cores - the bug Phase 6 / discovered-#2 fixes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private sealed class TwoLongs
    {
        public long A;
        public long B;
    }
}
