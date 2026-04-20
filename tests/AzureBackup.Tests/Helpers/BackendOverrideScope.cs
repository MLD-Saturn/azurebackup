using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Per-async-flow helper that pins
/// <see cref="DatabaseBackendFactory.ShouldUseSqlite"/> to a fixed value
/// for the lifetime of the <c>using</c> block, then clears it on dispose.
///
/// <para>
/// Replaces the older <c>EnvVarScope</c> pattern that mutated the
/// process-wide <c>AZBK_USE_SQLITE</c> environment variable. The env-var
/// pattern was unsafe under xUnit's parallel test execution: two test
/// classes that flipped the env var concurrently would race, and a
/// third test in a sibling class could observe the wrong value mid-call.
/// The <see cref="AsyncLocal{T}"/>-based override is per-logical-thread
/// so each test is isolated.
/// </para>
///
/// <para>
/// Production code never uses this helper. It exists exclusively to
/// route a test's <see cref="LocalDatabaseService.Initialize"/> calls
/// through one specific backend without affecting siblings.
/// </para>
/// </summary>
internal sealed class BackendOverrideScope : IDisposable
{
    public BackendOverrideScope(bool useSqlite)
    {
        DatabaseBackendFactory.SetAsyncLocalOverride(useSqlite);
    }

    public void Dispose()
    {
        DatabaseBackendFactory.SetAsyncLocalOverride(null);
    }
}
