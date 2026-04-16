using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 2 / P4: demonstrates the O(1) round-trip savings of the listing-based
/// collision resolver over the legacy linear <c>ExistsAsync</c> probe.
///
/// <para>
/// Azure round-trip latency is simulated (not real HTTP) because the point is
/// the count of round trips, not the bytes on the wire. Each simulated round
/// trip is a <see cref="Task.Delay"/> of <see cref="LatencyMs"/> milliseconds —
/// representative of a typical 20-50 ms east-US RTT.
/// </para>
///
/// <para>
/// Both variants assume every existing version stores different data, so no
/// dedup short-circuit is triggered. This models the worst case for the legacy
/// probe (walk every existing version before picking the next slot).
/// </para>
/// </summary>
[MemoryDiagnoser]
public class CollisionProbeBenchmark
{
    private const int LatencyMs = 20;

    /// <summary>
    /// Number of existing collision versions (_v2.._v{N+1}) to simulate.
    /// </summary>
    [Params(1, 10, 50)]
    public int ExistingCollisions { get; set; }

    /// <summary>
    /// Legacy: linear <c>ExistsAsync</c> per version until a free slot is found.
    /// Cost = (N + 1) round trips.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Legacy: linear ExistsAsync probe")]
    public async Task<int> Legacy_LinearProbe()
    {
        var roundTrips = 0;
        for (int version = 2; version <= ExistingCollisions + 2; version++)
        {
            // ExistsAsync round trip
            await Task.Delay(LatencyMs);
            roundTrips++;

            // For the first N versions the slot is occupied; on the (N+1)th call it's free.
            if (version == ExistingCollisions + 2)
            {
                return roundTrips;
            }
        }
        return roundTrips;
    }

    /// <summary>
    /// Phase 2: single listing call returns every existing <c>_vN</c>.
    /// Cost = 1 round trip, regardless of N.
    /// </summary>
    [Benchmark(Description = "Phase2: single listing call")]
    public async Task<int> Phase2_ListingProbe()
    {
        // One GetBlobsAsync round trip that returns all existing versions.
        await Task.Delay(LatencyMs);
        return 1;
    }
}
