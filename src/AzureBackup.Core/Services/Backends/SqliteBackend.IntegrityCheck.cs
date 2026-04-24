using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    #region Integrity Check Persistence (D1)

    /// <summary>
    /// Inserts a new run row at the start of an integrity check.
    /// Returns the auto-generated id so the engine can stamp it on
    /// every failure record it produces during the run.
    /// </summary>
    public int InsertIntegrityCheckRun(IntegrityCheckRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");
        EmitDiag($"InsertIntegrityCheckRun: enter (scope={run.ScopeSummary}, parentRunId={run.ParentRunId?.ToString() ?? "(none)"})");
        try
        {
            return InWriteLock(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO integrity_check_runs
                        (started_utc, finished_utc, session_id, scope_summary,
                         files_checked, files_passed, files_failed_t1,
                         files_failed_t2, files_failed_t3, files_warning,
                         cancelled, parent_run_id, diag_bundle_path)
                    VALUES
                        ($started, $finished, $session, $scope,
                         $checked, $passed, $f1, $f2, $f3, $warn,
                         $cancelled, $parent, $bundle);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$started", run.StartedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$finished", (object?)run.FinishedUtc?.ToString("o") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$session", run.SessionId.ToString("N"));
                cmd.Parameters.AddWithValue("$scope", run.ScopeSummary);
                cmd.Parameters.AddWithValue("$checked", run.FilesChecked);
                cmd.Parameters.AddWithValue("$passed", run.FilesPassed);
                cmd.Parameters.AddWithValue("$f1", run.FilesFailedT1);
                cmd.Parameters.AddWithValue("$f2", run.FilesFailedT2);
                cmd.Parameters.AddWithValue("$f3", run.FilesFailedT3);
                cmd.Parameters.AddWithValue("$warn", run.FilesWarning);
                cmd.Parameters.AddWithValue("$cancelled", run.Cancelled ? 1 : 0);
                cmd.Parameters.AddWithValue("$parent", (object?)run.ParentRunId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bundle", (object?)run.DiagBundlePath ?? DBNull.Value);
                var id = Convert.ToInt32(cmd.ExecuteScalar());
                run.Id = id;
                return id;
            });
        }
        catch (Exception ex)
        {
            EmitDiag($"InsertIntegrityCheckRun: FAILED with {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Updates the run row at the end of a check (or on cancellation) with
    /// final counters. The row is identified by <see cref="IntegrityCheckRun.Id"/>.
    /// </summary>
    public void UpdateIntegrityCheckRun(IntegrityCheckRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Id <= 0) throw new InvalidOperationException("Run must have an Id (call InsertIntegrityCheckRun first)");
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");
        try
        {
            InWriteLock(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    UPDATE integrity_check_runs SET
                        finished_utc = $finished,
                        files_checked = $checked,
                        files_passed = $passed,
                        files_failed_t1 = $f1,
                        files_failed_t2 = $f2,
                        files_failed_t3 = $f3,
                        files_warning = $warn,
                        cancelled = $cancelled,
                        diag_bundle_path = $bundle
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$finished", (object?)run.FinishedUtc?.ToString("o") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$checked", run.FilesChecked);
                cmd.Parameters.AddWithValue("$passed", run.FilesPassed);
                cmd.Parameters.AddWithValue("$f1", run.FilesFailedT1);
                cmd.Parameters.AddWithValue("$f2", run.FilesFailedT2);
                cmd.Parameters.AddWithValue("$f3", run.FilesFailedT3);
                cmd.Parameters.AddWithValue("$warn", run.FilesWarning);
                cmd.Parameters.AddWithValue("$cancelled", run.Cancelled ? 1 : 0);
                cmd.Parameters.AddWithValue("$bundle", (object?)run.DiagBundlePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", run.Id);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            EmitDiag($"UpdateIntegrityCheckRun: FAILED with {ex.GetType().Name}: {ex.Message} (runId={run.Id}, cancelled={run.Cancelled})");
            throw;
        }
    }

    public void InsertIntegrityCheckFailure(IntegrityCheckFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");
        try
        {
            InWriteLock(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO integrity_check_failures
                        (run_id, file_id, local_path, failure_tier,
                         failure_reason, chunk_hash, detail, diag_file_path)
                    VALUES
                        ($run, $file, $path, $tier, $reason, $chunk, $detail, $diag);
                    """;
                cmd.Parameters.AddWithValue("$run", failure.RunId);
                cmd.Parameters.AddWithValue("$file", failure.FileId);
                cmd.Parameters.AddWithValue("$path", failure.LocalPath);
                cmd.Parameters.AddWithValue("$tier", failure.FailureTier);
                cmd.Parameters.AddWithValue("$reason", failure.FailureReason);
                cmd.Parameters.AddWithValue("$chunk", (object?)failure.ChunkHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$detail", failure.Detail);
                cmd.Parameters.AddWithValue("$diag", (object?)failure.DiagFilePath ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            EmitDiag($"InsertIntegrityCheckFailure: FAILED with {ex.GetType().Name}: {ex.Message} (runId={failure.RunId}, tier={failure.FailureTier}, path={failure.LocalPath})");
            throw;
        }
    }

    /// <summary>
    /// Returns runs ordered newest-first. Used by the Data Integrity tab's
    /// History expander (limit ~10 visible).
    /// </summary>
    public List<IntegrityCheckRun> GetRecentIntegrityCheckRuns(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var results = new List<IntegrityCheckRun>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, started_utc, finished_utc, session_id, scope_summary,
                       files_checked, files_passed, files_failed_t1,
                       files_failed_t2, files_failed_t3, files_warning,
                       cancelled, parent_run_id, diag_bundle_path
                FROM integrity_check_runs
                ORDER BY started_utc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadRun(reader));
            }
            return results;
        });
    }

    /// <summary>
    /// Returns failure rows for a specific run. The integrity-check engine
    /// only ever populates this table with rows for the latest run, but the
    /// query is keyed by run id for safety.
    /// </summary>
    public List<IntegrityCheckFailure> GetIntegrityCheckFailures(int runId)
    {
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var results = new List<IntegrityCheckFailure>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, run_id, file_id, local_path, failure_tier,
                       failure_reason, chunk_hash, detail, diag_file_path
                FROM integrity_check_failures
                WHERE run_id = $run
                ORDER BY failure_tier DESC, local_path ASC;
                """;
            cmd.Parameters.AddWithValue("$run", runId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadFailure(reader));
            }
            return results;
        });
    }

    /// <summary>
    /// Deletes every failure row not belonging to <paramref name="keepRunId"/>.
    /// Called by the engine at the start of each new run so the failures
    /// table only ever holds rows for the latest run -- bounded UI cost
    /// regardless of how many historical runs the user has accumulated.
    /// </summary>
    public void DeleteIntegrityCheckFailuresExcept(int keepRunId)
    {
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");
        InWriteLock(() =>
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM integrity_check_failures WHERE run_id <> $keep;";
            cmd.Parameters.AddWithValue("$keep", keepRunId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes run rows beyond the most recent <paramref name="keep"/>.
    /// Each row is ~1 KB; we keep 30 by default but the engine takes
    /// the limit so tests can use 5 without polluting their DB.
    /// </summary>
    public void PruneIntegrityCheckRuns(int keep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        if (_connection == null) throw new InvalidOperationException("Backend is not initialized.");
        InWriteLock(() =>
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                DELETE FROM integrity_check_runs
                WHERE id NOT IN (
                    SELECT id FROM integrity_check_runs
                    ORDER BY started_utc DESC
                    LIMIT $keep
                );
                """;
            cmd.Parameters.AddWithValue("$keep", keep);
            cmd.ExecuteNonQuery();
        });
    }

    private static IntegrityCheckRun ReadRun(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new IntegrityCheckRun
        {
            Id = reader.GetInt32(0),
            StartedUtc = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            FinishedUtc = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            SessionId = Guid.TryParseExact(reader.GetString(3), "N", out var sid) ? sid : Guid.Empty,
            ScopeSummary = reader.GetString(4),
            FilesChecked = reader.GetInt32(5),
            FilesPassed = reader.GetInt32(6),
            FilesFailedT1 = reader.GetInt32(7),
            FilesFailedT2 = reader.GetInt32(8),
            FilesFailedT3 = reader.GetInt32(9),
            FilesWarning = reader.GetInt32(10),
            Cancelled = reader.GetInt32(11) != 0,
            ParentRunId = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            DiagBundlePath = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }

    private static IntegrityCheckFailure ReadFailure(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new IntegrityCheckFailure
        {
            Id = reader.GetInt32(0),
            RunId = reader.GetInt32(1),
            FileId = reader.GetInt32(2),
            LocalPath = reader.GetString(3),
            FailureTier = reader.GetInt32(4),
            FailureReason = reader.GetString(5),
            ChunkHash = reader.IsDBNull(6) ? null : reader.GetString(6),
            Detail = reader.GetString(7),
            DiagFilePath = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    #endregion
}
