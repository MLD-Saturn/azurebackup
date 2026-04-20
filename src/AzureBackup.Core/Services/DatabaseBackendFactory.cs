using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Option C / C-1 final step b: decides at
/// <see cref="LocalDatabaseService.Initialize(string, ReadOnlySpan{char})"/>
/// time which backend the service delegates to.
///
/// <para>
/// The flag is the environment variable <c>AZBK_USE_SQLITE</c>. Values
/// <c>1</c>, <c>true</c>, <c>yes</c>, and <c>on</c> (case-insensitive,
/// trimmed) enable the SQLite backend. Any other value - including
/// unset, empty, <c>0</c>, <c>false</c> - leaves the service on its
/// original LiteDB code path.
/// </para>
///
/// <para>
/// <b>Default-off is the safe choice.</b> All existing users and all
/// existing integration tests see the LiteDB path by default. A single
/// env-var flip routes them to SQLite with zero other changes. This is
/// the preview-flag gate the eval doc \u00a711.8 prescribes for the C-6
/// soak phase.
/// </para>
///
/// <para>
/// <b>Read once, not per call.</b> The factory is consulted exactly
/// once per <c>Initialize</c> call; flipping the env var mid-session
/// is undefined. Production code never flips it, and tests that need
/// to exercise both paths should run in separate processes (xunit
/// creates separate AppDomains per test class which is sufficient).
/// </para>
/// </summary>
internal static class DatabaseBackendFactory
{
    /// <summary>
    /// Name of the environment variable that routes a user to the
    /// SQLite backend. Kept as a public <c>const string</c> so tests
    /// can set/unset it without magic-string drift.
    /// </summary>
    public const string EnvironmentVariableName = "AZBK_USE_SQLITE";

    /// <summary>
    /// Optional per-async-flow override that bypasses the process-wide
    /// <see cref="EnvironmentVariableName"/> env var. <c>null</c> means
    /// "fall back to the env var"; a non-null value pins the backend
    /// choice for the current async context only.
    ///
    /// <para>
    /// <b>Why <see cref="AsyncLocal{T}"/>:</b> the env-var path is a
    /// process-wide global that xUnit's parallel test runner cannot
    /// safely share. Two test classes that flip the env var in parallel
    /// would race; one test would see the other's value mid-call. The
    /// <see cref="AsyncLocal{T}"/> field is per-logical-thread (and
    /// inherited by tasks spawned within), so each test that opts in
    /// gets its own pinned choice without affecting siblings.
    /// </para>
    ///
    /// <para>
    /// Production code never sets this. Tests use the
    /// <c>BackendOverrideScope</c> helper which sets and clears the
    /// value via <c>using</c>.
    /// </para>
    /// </summary>
    private static readonly AsyncLocal<bool?> _asyncLocalOverride = new();

    /// <summary>
    /// Test hook: pins <see cref="ShouldUseSqlite"/> to a fixed value
    /// for the current async flow. Pass <c>null</c> to clear the
    /// override and fall back to the env var.
    /// </summary>
    /// <remarks>
    /// Internal-only on purpose - production must not depend on
    /// per-async-flow backend choices. Tests reach this via
    /// <c>InternalsVisibleTo</c> on AzureBackup.Tests.
    /// </remarks>
    internal static void SetAsyncLocalOverride(bool? value)
    {
        _asyncLocalOverride.Value = value;
    }

    /// <summary>
    /// Returns <c>true</c> if the current process environment is
    /// configured to use the SQLite backend. The
    /// <see cref="AsyncLocal{T}"/> override (if set) takes precedence
    /// over the env var.
    /// </summary>
    public static bool ShouldUseSqlite()
    {
        var pinned = _asyncLocalOverride.Value;
        if (pinned.HasValue) return pinned.Value;

        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return IsTruthy(raw);
    }

    /// <summary>
    /// Creates and initialises a <see cref="SqliteBackend"/> against
    /// the given database path and password. Kept as a factory method
    /// so <see cref="LocalDatabaseService"/> does not need a
    /// <c>using</c> on the Backends namespace.
    /// </summary>
    public static SqliteBackend CreateAndInitializeSqlite(
        string databasePath, ReadOnlySpan<char> password)
    {
        var backend = new SqliteBackend();
        backend.Initialize(databasePath, password);
        return backend;
    }

    /// <summary>
    /// Env-var truthy check. Accepts <c>1</c>, <c>true</c>, <c>yes</c>,
    /// <c>on</c> (any case) as truthy. Anything else, including
    /// <c>null</c>, empty, and whitespace, is falsy.
    /// </summary>
    internal static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        return trimmed.Equals("1", StringComparison.Ordinal)
            || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
