using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- Aggregate statistics ----------------------------------------------

    /// <summary>
    /// Single-pass aggregate over <c>files</c>, <c>pending_changes</c>, and
    /// the singleton <c>config</c> row. The LiteDB equivalent materialised
    /// every BackedUpFile into memory and aggregated in C# (.FindAll().ToList()
    /// followed by Count + Sum); the SQLite version does the work in three
    /// indexed queries with no intermediate object allocation.
    ///
    /// <para>
    /// Conditional sums in the files query use SQLite's standard pattern
    /// <c>SUM(CASE WHEN ... THEN 1 ELSE 0 END)</c>. The status column is
    /// indexed so each branch is an indexed seek over the distinct status
    /// values.
    /// </para>
    /// </summary>
    public BackupStatistics GetStatistics()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var stats = new BackupStatistics();

            // --- files: count + size + per-status breakdown in one query ---
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT
                        COUNT(*),
                        COALESCE(SUM(file_size), 0),
                        COALESCE(SUM(CASE WHEN status = {(int)BackupStatus.Completed} THEN 1 ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN status = {(int)BackupStatus.Pending}   THEN 1 ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN status = {(int)BackupStatus.Failed}    THEN 1 ELSE 0 END), 0)
                    FROM files;
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    stats.TotalFiles = reader.GetInt32(0);
                    stats.TotalSize = reader.GetInt64(1);
                    stats.CompletedFiles = reader.GetInt32(2);
                    stats.PendingFiles = reader.GetInt32(3);
                    stats.FailedFiles = reader.GetInt32(4);
                }
            }

            // --- pending_changes: trivial count ---
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pending_changes;";
                stats.PendingChanges = Convert.ToInt32(cmd.ExecuteScalar());
            }

            // --- config: just the two scalar fields the stats panel cares about
            // (LastBackupTime + TotalBytesUploaded). Read directly rather than
            // re-using GetConfiguration() to avoid loading the watched-folder
            // and global-exclude lists for a hot status-bar refresh.
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT last_backup_time, total_bytes_uploaded
                    FROM config WHERE id = 1;
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    stats.LastBackupTime = reader.IsDBNull(0) ? null : ParseUtc(reader.GetString(0));
                    stats.TotalBytesUploaded = reader.GetInt64(1);
                }
            }

            return stats;
        });
    }

    /// <summary>
    /// Closes the underlying connection and releases native handles.
    /// Safe to call multiple times.
    /// </summary>
}
