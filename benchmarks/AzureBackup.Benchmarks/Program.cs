using BenchmarkDotNet.Running;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Entry point for local-developer benchmarks. Not run in CI.
///
/// Usage:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks
///
/// Or to filter to a single benchmark:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks -- --filter *Phase1*
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
