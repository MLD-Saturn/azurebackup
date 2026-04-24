using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- Pending changes queue ---------------------------------------------

    /// <summary>
    /// Inserts a single change, replacing any pending row that already
    /// targets the same file. Replace + insert run inside one transaction
    /// so a reader never sees zero pending entries for a path that has
    /// always had at least one pending change.
    /// </summary>
    public void QueueFileChange(FileChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var tx = _connection.BeginTransaction();

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM pending_changes WHERE file_path = $file_path;";
                clear.Parameters.AddWithValue("$file_path", change.FilePath);
                clear.ExecuteNonQuery();
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO pending_changes (file_path, change_type, detected_at)
                    VALUES ($file_path, $change_type, $detected_at);
                    """;
                insert.Parameters.AddWithValue("$file_path", change.FilePath);
                insert.Parameters.AddWithValue("$change_type", (int)change.ChangeType);
                insert.Parameters.AddWithValue("$detected_at", FormatUtc(change.DetectedAt));
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        });
    }

    /// <summary>
    /// Bulk variant: deduplicates the input by FilePath (last-write-wins
    /// using OrdinalIgnoreCase, matching the LiteDB-era behavior) and
    /// performs all DELETE + INSERT work in a single transaction.
    ///
    /// <para>
    /// At ~10K changes (e.g. IDE rebuild, git checkout) this turns ~10K
    /// micro-transactions into one, cutting commit overhead by 1-2 orders
    /// of magnitude. The DELETE per affected path uses the indexed
    /// file_path column so each is O(log N).
    /// </para>
    /// </summary>
    public void QueueFileChangesBatch(IEnumerable<FileChangeEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        var byPath = new Dictionary<string, FileChangeEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            if (c is null) continue;
            byPath[c.FilePath] = c;
        }
        if (byPath.Count == 0) return;

        InWriteLock(() =>
        {
            using var tx = _connection.BeginTransaction();

            // DELETE per affected path. SQLite's expression engine has no
            // equivalent of LiteDB's "Contains not supported" pitfall, so we
            // could batch this with `WHERE file_path IN (?, ?, ...)` instead.
            // For now the per-path loop matches the LiteDB code path and runs
            // off the indexed file_path column - O(log N) per DELETE.
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM pending_changes WHERE file_path = $file_path;";
                var pPath = del.Parameters.Add("$file_path", SqliteType.Text);
                foreach (var path in byPath.Keys)
                {
                    pPath.Value = path;
                    del.ExecuteNonQuery();
                }
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO pending_changes (file_path, change_type, detected_at)
                    VALUES ($file_path, $change_type, $detected_at);
                    """;
                var pPath = insert.Parameters.Add("$file_path", SqliteType.Text);
                var pType = insert.Parameters.Add("$change_type", SqliteType.Integer);
                var pTime = insert.Parameters.Add("$detected_at", SqliteType.Text);

                foreach (var c in byPath.Values)
                {
                    pPath.Value = c.FilePath;
                    pType.Value = (int)c.ChangeType;
                    pTime.Value = FormatUtc(c.DetectedAt);
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
        });
    }

    /// <summary>
    /// Returns the next batch of pending changes ordered by DetectedAt ASC.
    /// Uses LIMIT to bound memory; LiteDB equivalent achieved the same
    /// via .OrderBy().Take().
    /// </summary>
    /// <remarks>
    /// B17: <paramref name="batchSize"/> is the SQL LIMIT cap, NOT the
    /// initial allocation size of the result list. Pre-B17 the list ctor
    /// was called with the raw batchSize, which produced an OOM when the
    /// caller passed <c>int.MaxValue</c> as a "no limit" sentinel
    /// (List&lt;T&gt;(2147483647) tries to allocate ~51 GB of references).
    /// We now clamp the pre-allocation to a small ceiling and let the
    /// list grow naturally as rows are read.
    /// </remarks>
    public List<FileChangeEvent> GetPendingChanges(int batchSize = 100)
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");
        if (batchSize <= 0) batchSize = 100;

        // B17: bound the up-front allocation. 1024 covers the 99th-pct
        // batch for the FileChangeEvent worker without needing to grow.
        const int InitialCapacityCeiling = 1024;
        var initialCapacity = Math.Min(batchSize, InitialCapacityCeiling);

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<FileChangeEvent>(initialCapacity);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT file_path, change_type, detected_at
                FROM pending_changes
                ORDER BY detected_at ASC
                LIMIT $batch_size;
                """;
            cmd.Parameters.AddWithValue("$batch_size", batchSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new FileChangeEvent
                {
                    FilePath = reader.GetString(0),
                    ChangeType = (FileChangeType)reader.GetInt32(1),
                    DetectedAt = ParseUtc(reader.GetString(2)),
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Deletes every pending row whose file_path matches. No-op when no
    /// rows match (mirrors LiteDB DeleteMany semantics).
    /// </summary>
    public void RemovePendingChange(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_changes WHERE file_path = $file_path;";
            cmd.Parameters.AddWithValue("$file_path", filePath);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns every pending file path as an OrdinalIgnoreCase set so
    /// callers can answer "is X pending?" without one round-trip per
    /// check.
    /// </summary>
    public HashSet<string> GetAllPendingChangePaths()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT file_path FROM pending_changes;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        });
    }

}
