using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;

namespace AzureBackup.Benchmarks;

/// <summary>
/// W5 Phase 1 (B64): BenchmarkDotNet config that includes the
/// W5 memory-fidelity columns alongside BDN's defaults.
///
/// <para>
/// Applied via <c>[Config(typeof(MemoryFidelityConfig))]</c> on the
/// benchmark types that exercise the memory-budget path. Inherits
/// the default column providers (so <c>Mean</c>, <c>Error</c>,
/// <c>StdDev</c>, <c>Allocated</c>, etc. still appear) and adds the
/// six fidelity columns from
/// <see cref="MemoryFidelityColumnProvider"/>.
/// </para>
///
/// <para>
/// Why a dedicated config rather than the default. BDN's default
/// pipeline does not include <see cref="MemoryFidelityColumnProvider"/>;
/// adding it via the assembly attribute would surface fidelity
/// columns on every benchmark in the project, including ones that
/// do not run a backup (e.g. <c>CdcRollingHashBenchmark</c>),
/// where every cell would render as "-" and pollute the output.
/// Opting in per-benchmark keeps the new columns visible only where
/// they are meaningful.
/// </para>
/// </summary>
public sealed class MemoryFidelityConfig : ManualConfig
{
    public MemoryFidelityConfig()
    {
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumnProvider(MemoryFidelityColumnProvider.Instance);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
