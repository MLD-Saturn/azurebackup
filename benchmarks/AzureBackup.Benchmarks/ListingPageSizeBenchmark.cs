using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Phase 3 / discovered-item #1: measures the round-trip savings of the explicit
/// <c>pageSizeHint</c> on <c>GetBlobsAsync</c>.
///
/// <para>
/// Azure's default page size is 5000 blobs, so the win here is small for most
/// containers. At very large scale (millions of chunks) a larger page size can
/// still help by reducing the number of HTTP round trips. This benchmark models
/// that behaviour by simulating per-page latency.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ListingPageSizeBenchmark
{
    private const int PerPageLatencyMs = 20;
    private const int TotalBlobs = 100_000;

    [Params(1000, 5000, 10000)]
    public int PageSize { get; set; }

    [Benchmark(Description = "Simulated GetBlobsAsync pagination")]
    public async Task<int> PaginatedListing()
    {
        var pages = (TotalBlobs + PageSize - 1) / PageSize;
        for (int i = 0; i < pages; i++)
        {
            // Each page is one Azure round trip.
            await Task.Delay(PerPageLatencyMs);
        }
        return pages;
    }
}
