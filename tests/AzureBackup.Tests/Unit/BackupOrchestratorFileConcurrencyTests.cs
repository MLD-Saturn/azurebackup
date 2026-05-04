using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B54 (W3 Phase C): tests for the budget-derived file-level concurrency
/// helper on <see cref="BackupOrchestrator"/>. The helper clamps the
/// configured file fan-out against the active <see cref="MemoryBudget"/>
/// so a small <c>MemoryLimitMB</c> reduces in-flight residency instead of
/// admitting many files that would all immediately stall.
/// </summary>
public class BackupOrchestratorFileConcurrencyTests
{
    private const long MB = 1024L * 1024;
    private const long PerFile = 512L * MB; // EstimatedPerFileResidencyBytes

    [Fact]
    public void UnlimitedBudget_ReturnsConfiguredCeiling()
    {
        using var budget = new MemoryBudget(long.MaxValue);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 16);

        Assert.Equal(16, result);
    }

    [Fact]
    public void BudgetWellAboveCeiling_ReturnsConfiguredCeiling()
    {
        // 32 GB budget / 512 MB per file = 64 by-budget, capped at 16.
        using var budget = new MemoryBudget(32L * 1024 * MB);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 16);

        Assert.Equal(16, result);
    }

    [Fact]
    public void BudgetEqualsCeilingTimesPerFile_ReturnsCeilingExactly()
    {
        // 16 * 512 MB = 8 GB. by-budget = 16 = ceiling.
        using var budget = new MemoryBudget(16 * PerFile);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 16);

        Assert.Equal(16, result);
    }

    [Theory]
    [InlineData(8L * 1024, 16, 16)]      // 8 GB -> 16
    [InlineData(4L * 1024, 16, 8)]       // 4 GB -> 8
    [InlineData(2L * 1024, 16, 4)]       // 2 GB -> 4
    [InlineData(1L * 1024, 16, 2)]       // 1 GB -> 2
    [InlineData(512, 16, 1)]              // 512 MB -> 1 (below per-file)
    [InlineData(256, 16, 1)]              // 256 MB -> floor of 1
    public void BudgetBelowOrAtCeiling_ScalesDown(long budgetMb, int ceiling, int expected)
    {
        using var budget = new MemoryBudget(budgetMb * MB);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: ceiling);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TinyBudget_ReturnsFloorOfOne()
    {
        // Even a 1-byte budget must never starve to zero.
        using var budget = new MemoryBudget(1);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 16);

        Assert.Equal(1, result);
    }

    [Fact]
    public void NullBudget_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => BackupOrchestrator.ComputeEffectiveFileConcurrency(null!, configuredCeiling: 16));
    }

    [Fact]
    public void ZeroCeiling_Throws()
    {
        using var budget = new MemoryBudget(8L * 1024 * MB);

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 0));
    }

    [Fact]
    public void NegativeCeiling_Throws()
    {
        using var budget = new MemoryBudget(8L * 1024 * MB);

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: -1));
    }

    [Fact]
    public void CeilingOfOne_AlwaysReturnsOne()
    {
        // Ceiling lower than the budget would allow -- we still respect it.
        using var budget = new MemoryBudget(64L * 1024 * MB);

        var result = BackupOrchestrator.ComputeEffectiveFileConcurrency(budget, configuredCeiling: 1);

        Assert.Equal(1, result);
    }
}
