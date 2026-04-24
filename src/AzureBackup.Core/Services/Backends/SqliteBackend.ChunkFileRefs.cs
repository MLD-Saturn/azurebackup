using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- chunk_file_refs internal helpers ----------------------------------
    //
    // These five methods exist as internal counterparts to the same-named
    // helpers on LocalDatabaseService. They are NOT on IDatabaseBackend
    // because they are an implementation detail of the chunk-tracking layer
    // (ChunkIndexService.AddReference / RemoveReference). LocalDatabaseService
    // delegates to these when its _sqliteBackend field is set; benchmark
    // and contract code never touches them.
    //
    // Acquire the write lock on every call to mirror LiteDB's
    // InWriteLock semantics: ChunkIndexService runs from parallel
    // backup-loop tasks, and SqliteConnection has no internal write
    // serialization.

    /// <summary>
    /// Idempotent insert of a single (file_path, chunk_hash, chunk_index)
    /// reverse-index row. If the triple already exists, only the
    /// referenced_at timestamp is updated. Mirrors
    /// <c>LocalDatabaseService.UpsertChunkFileRef</c>.
    /// </summary>
    internal void UpsertChunkFileRef(string filePath, string chunkHash, int chunkIndex, DateTime referencedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        _writeLock.EnterWriteLock();
        try
        {
            // Manual upsert: chunk_file_refs has no UNIQUE constraint over
            // (file_path, chunk_hash, chunk_index) so ON CONFLICT does not
            // apply. The DELETE-then-INSERT pair runs in a transaction so
            // a reader never sees a missing row. The DELETE uses the
            // (file_path) index and is cheap.
            using var tx = _connection.BeginTransaction();
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM chunk_file_refs
                    WHERE file_path = $file_path
                      AND chunk_hash = $chunk_hash
                      AND chunk_index = $chunk_index;
                    """;
                del.Parameters.AddWithValue("$file_path", filePath);
                del.Parameters.AddWithValue("$chunk_hash", chunkHash);
                del.Parameters.AddWithValue("$chunk_index", chunkIndex);
                del.ExecuteNonQuery();
            }
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO chunk_file_refs (file_path, chunk_hash, chunk_index, referenced_at)
                    VALUES ($file_path, $chunk_hash, $chunk_index, $referenced_at);
                    """;
                ins.Parameters.AddWithValue("$file_path", filePath);
                ins.Parameters.AddWithValue("$chunk_hash", chunkHash);
                ins.Parameters.AddWithValue("$chunk_index", chunkIndex);
                ins.Parameters.AddWithValue("$referenced_at", FormatUtc(referencedAt));
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Bulk-insert reverse-index rows in a single transaction. Mirrors
    /// <c>LocalDatabaseService.BulkInsertChunkFileRefs</c>.
    /// </summary>
    internal void BulkInsertChunkFileRefs(IEnumerable<ChunkFileRefRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B22: materialise so the diag can report row count
        var rowList = rows as IList<ChunkFileRefRow> ?? rows.ToList();
        EmitDiag($"BulkInsertChunkFileRefs: enter ({rowList.Count} rows)");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _writeLock.EnterWriteLock();
        try
        {
            using var tx = _connection.BeginTransaction();
            using var ins = _connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO chunk_file_refs (file_path, chunk_hash, chunk_index, referenced_at)
                VALUES ($file_path, $chunk_hash, $chunk_index, $referenced_at);
                """;
            var p_path = ins.Parameters.Add("$file_path", SqliteType.Text);
            var p_hash = ins.Parameters.Add("$chunk_hash", SqliteType.Text);
            var p_index = ins.Parameters.Add("$chunk_index", SqliteType.Integer);
            var p_time = ins.Parameters.Add("$referenced_at", SqliteType.Text);
            foreach (var row in rowList)
            {
                p_path.Value = row.FilePath;
                p_hash.Value = row.ChunkHash;
                p_index.Value = row.ChunkIndex;
                p_time.Value = FormatUtc(row.ReferencedAt);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            EmitDiag($"BulkInsertChunkFileRefs: commit OK ({rowList.Count} rows in {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            EmitDiag($"BulkInsertChunkFileRefs: FAILED with {ex.GetType().Name}: {ex.Message} after {sw.ElapsedMilliseconds} ms ({rowList.Count} rows)");
            throw;
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Returns every row in <c>chunk_file_refs</c>. Used by the
    /// Azure index backup path so the encrypted blob carries the
    /// reverse-index alongside the primary chunk_index. Without
    /// this, RestoreIndexFromAzureAsync would lose every reference
    /// (under SQLite GetAllChunkIndexEntries leaves
    /// ChunkIndexEntry.ReferencingFiles empty by design).
    /// </summary>
    internal List<ChunkFileRefRow> GetAllChunkFileRefs()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<ChunkFileRefRow>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT file_path, chunk_hash, chunk_index, referenced_at
                FROM chunk_file_refs;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChunkFileRefRow
                {
                    FilePath = reader.GetString(0),
                    ChunkHash = reader.GetString(1),
                    ChunkIndex = reader.GetInt32(2),
                    ReferencedAt = ParseUtc(reader.GetString(3)),
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Deletes every reverse-index row for a single file path. Returns
    /// the number of rows deleted. Mirrors
    /// <c>LocalDatabaseService.DeleteChunkFileRefsForFile</c>.
    /// </summary>
    internal int DeleteChunkFileRefsForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        _writeLock.EnterWriteLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chunk_file_refs WHERE file_path = $file_path;";
            cmd.Parameters.AddWithValue("$file_path", filePath);
            return cmd.ExecuteNonQuery();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Deletes every reverse-index row for a single chunk hash. Returns
    /// the number of rows deleted. Mirrors
    /// <c>LocalDatabaseService.DeleteChunkFileRefsForChunk</c>.
    /// </summary>
    internal int DeleteChunkFileRefsForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        _writeLock.EnterWriteLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chunk_file_refs WHERE chunk_hash = $chunk_hash;";
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            return cmd.ExecuteNonQuery();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Deletes reverse-index rows binding a specific file path to a
    /// specific chunk hash. Returns the number of rows deleted. Mirrors
    /// <c>LocalDatabaseService.DeleteChunkFileRefsForFileAndChunk</c>.
    /// </summary>
    internal int DeleteChunkFileRefsForFileAndChunk(string filePath, string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        _writeLock.EnterWriteLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM chunk_file_refs
                WHERE file_path = $file_path AND chunk_hash = $chunk_hash;
                """;
            cmd.Parameters.AddWithValue("$file_path", filePath);
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            return cmd.ExecuteNonQuery();
        }
        finally { _writeLock.ExitWriteLock(); }
    }

    private List<ChunkInfo> LoadChunksForFile(long fileId)
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        var chunks = new List<ChunkInfo>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT chunk_index, offset, length, hash, blob_name
            FROM file_chunks
            WHERE file_id = $file_id
            ORDER BY chunk_order;
            """;
        cmd.Parameters.AddWithValue("$file_id", fileId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new ChunkInfo
            {
                Index = reader.GetInt32(0),
                Offset = reader.GetInt64(1),
                Length = reader.GetInt32(2),
                Hash = reader.GetString(3),
                BlobName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            });
        }
        return chunks;
    }


    // ---- Reverse chunk index (chunk_file_refs) -----------------------------

    // Sentinel key in index_metadata that flips to a non-null timestamp once
    // the reverse-index rebuild completes. Reads are O(1) on the primary key.
    private const string ReverseIndexSentinelKey = "ReverseIndexBuiltAt";

    /// <summary>
    /// Returns the chunk-index rows referenced by <paramref name="filePath"/>.
    /// Single SELECT joining chunk_file_refs (indexed on file_path) against
    /// chunk_index (primary-key keyed on chunk_hash). DISTINCT collapses
    /// the row count when a file references the same chunk multiple times,
    /// which the LiteDB-era code path also did implicitly.
    /// </summary>
    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<ChunkIndexEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT
                    ci.chunk_hash, ci.first_uploaded_at, ci.original_uploader_path,
                    ci.size_bytes, ci.reference_count, ci.current_tier, ci.last_verified_at
                FROM chunk_file_refs cfr
                INNER JOIN chunk_index ci ON ci.chunk_hash = cfr.chunk_hash
                WHERE cfr.file_path = $file_path;
                """;
            cmd.Parameters.AddWithValue("$file_path", filePath);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadChunkEntry(reader));
            }
            return result;
        });
    }

    /// <summary>
    /// O(1) sentinel lookup against index_metadata. Returns true once
    /// <see cref="RebuildReverseChunkIndex"/> has run successfully at
    /// least once for this database.
    /// </summary>
    public bool IsReverseChunkIndexBuilt()
        => GetIndexMetadata(ReverseIndexSentinelKey) != null;

    /// <summary>
    /// Backfills chunk_file_refs from file_chunks for any (file, chunk)
    /// pair that does not already have a matching reverse-index row. The
    /// SaveBackedUpFile path keeps the two tables in sync going forward,
    /// so this rebuild is exclusively for upgrade scenarios where a
    /// LiteDB-era database has been migrated and the reverse index has
    /// not yet been populated.
    ///
    /// <para>
    /// The method walks distinct file paths in batches so each write
    /// transaction stays short. Cancellation is honoured between batches.
    /// On cancellation the partial rows are NOT rolled back - the next
    /// invocation picks up where this one left off because the work loop
    /// inserts only the rows that are missing.
    /// </para>
    /// </summary>
    public void RebuildReverseChunkIndex(
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        if (IsReverseChunkIndexBuilt())
        {
            return;
        }

        InWriteLock(() => RebuildReverseChunkIndexCore(progress, cancellationToken));
    }

    private void RebuildReverseChunkIndexCore(
        IProgress<(int processed, int total)>? progress,
        CancellationToken cancellationToken)
    {
        // B22: this is one of the most expensive backend operations on
        // an upgrade-from-LiteDB code path; pre-B22 it ran in complete
        // silence which made debugging the rebuild feel like the app
        // had hung. Emit a breadcrumb at every observable phase so the
        // session log records:
        //   - candidate file count (work to do),
        //   - drop+recreate vs in-place choice,
        //   - INSERT...SELECT elapsed time,
        //   - checkpoint outcome,
        //   - sentinel write outcome.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EmitDiag("RebuildReverseChunkIndexCore: enter");

        // Snapshot the work list ONCE up front so the IProgress<>
        // contract has a real total to report against. The actual
        // rebuild then fires as a single INSERT...SELECT - the engine
        // does the JOIN, filtering, and bulk write entirely inside
        // the C layer with no managed-memory round-trips.
        cancellationToken.ThrowIfCancellationRequested();
        var total = 0;
        using (var countCmd = _connection.CreateCommand())
        {
            countCmd.CommandText = """
                SELECT COUNT(DISTINCT f.local_path)
                FROM files f
                INNER JOIN file_chunks fc ON fc.file_id = f.id
                WHERE NOT EXISTS (
                    SELECT 1 FROM chunk_file_refs cfr
                    WHERE cfr.file_path = f.local_path
                );
                """;
            total = Convert.ToInt32(countCmd.ExecuteScalar());
        }
        EmitDiag($"RebuildReverseChunkIndexCore: candidate file count = {total}");
        progress?.Report((0, total));

        // C-3 (3c) rewrite: previously this was a 256-file batched loop with
        // a per-batch INSERT...SELECT...WHERE local_path IN (...). The batching
        // was added in C-3 (3a) to amortise per-file fsyncs; once the per-file
        // fsync was gone, the batching itself became overhead (managed-memory
        // path-list slicing, per-batch placeholder string construction,
        // per-batch transaction begin/commit). C-3 (3b) measured the batched
        // version at 0.572 ratio at 500K - meaningful but not gate-clearing.
        //
        // Rewrite: ONE INSERT...SELECT for the whole rebuild, in ONE
        // transaction. NOT EXISTS preserves idempotency - cancelled or
        // partial prior runs naturally skip already-populated paths. The
        // engine does everything internally; no row materialisation in C#.
        cancellationToken.ThrowIfCancellationRequested();
        var referencedAt = FormatUtc(DateTime.UtcNow);

        // C-3 (3c-2) - I3: bulk-insert into an indexed table is significantly
        // slower than bulk-insert into an unindexed table because every row
        // touches every index. chunk_file_refs has TWO indexes
        // (idx_chunk_file_refs_path, idx_chunk_file_refs_hash) so each
        // INSERT does 1 table write + 2 index writes = 3x the disk pages
        // touched. Dropping the indexes, doing the bulk insert, then
        // recreating them in one shot is the standard "fast bulk load"
        // pattern.
        //
        // SAFETY GUARD: only do the drop+recreate when chunk_file_refs is
        // EMPTY. If a previous rebuild was cancelled mid-INSERT and left
        // partial rows, the NOT EXISTS clause in our SELECT needs the
        // path index to be cheap (otherwise it degenerates to a full
        // scan of chunk_file_refs for every candidate row in the JOIN).
        // The "empty table" check is a single-row count of pages 1-2
        // and costs microseconds; the optimisation when it applies saves
        // multi-second index-write cost at scale.
        var dropAndRecreate = false;
        using (var emptyCheck = _connection.CreateCommand())
        {
            emptyCheck.CommandText = "SELECT EXISTS(SELECT 1 FROM chunk_file_refs LIMIT 1);";
            var hasAnyRow = Convert.ToInt32(emptyCheck.ExecuteScalar()) == 1;
            dropAndRecreate = !hasAnyRow;
        }
        EmitDiag($"RebuildReverseChunkIndexCore: dropAndRecreate={dropAndRecreate} (chunk_file_refs is {(dropAndRecreate ? "EMPTY" : "non-empty -- preserving indexes for cheap NOT EXISTS")})");

        var insertSw = System.Diagnostics.Stopwatch.StartNew();

        using (var tx = _connection.BeginTransaction())
        {
            if (dropAndRecreate)
            {
                using var drop = _connection.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = """
                    DROP INDEX IF EXISTS idx_chunk_file_refs_path;
                    DROP INDEX IF EXISTS idx_chunk_file_refs_hash;
                    """;
                drop.ExecuteNonQuery();
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO chunk_file_refs
                        (file_path, chunk_hash, chunk_index, referenced_at)
                    SELECT f.local_path, fc.hash, fc.chunk_index, $referenced_at
                    FROM files f
                    INNER JOIN file_chunks fc ON fc.file_id = f.id
                    WHERE NOT EXISTS (
                        SELECT 1 FROM chunk_file_refs cfr
                        WHERE cfr.file_path = f.local_path
                    );
                    """;
                insert.Parameters.AddWithValue("$referenced_at", referencedAt);
                insert.ExecuteNonQuery();
            }

            if (dropAndRecreate)
            {
                // Recreate inside the same transaction so a crash mid-rebuild
                // leaves the schema consistent on rollback. SQLite builds the
                // index in one pass over the now-populated table.
                using var recreate = _connection.CreateCommand();
                recreate.Transaction = tx;
                recreate.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_path ON chunk_file_refs(file_path);
                    CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_hash ON chunk_file_refs(chunk_hash);
                    """;
                recreate.ExecuteNonQuery();
            }

            tx.Commit();
        }
        EmitDiag($"RebuildReverseChunkIndexCore: INSERT...SELECT + commit completed in {insertSw.ElapsedMilliseconds} ms");
        progress?.Report((total, total));

        // C-3 (3c) - I4: explicit WAL checkpoint after the rebuild. The
        // rebuild can produce tens of MB of WAL pages for the 500K case;
        // without an explicit checkpoint the next operation would absorb
        // the cost. TRUNCATE leaves the WAL file empty and zero-sized so
        // a subsequent measurement (or production query) starts from a
        // clean slate. Done OUTSIDE the rebuild transaction because
        // checkpoint requires no active transaction.
        using (var checkpointCmd = _connection.CreateCommand())
        {
            checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpointCmd.ExecuteNonQuery();
        }
        EmitDiag($"RebuildReverseChunkIndexCore: WAL checkpoint complete (total elapsed {sw.ElapsedMilliseconds} ms)");

        // Mark complete so future calls short-circuit. Done LAST so a
        // failure during checkpoint (very unlikely - WAL checkpoint with
        // no concurrent writer cannot fail) leaves the rebuild repeatable
        // rather than falsely marked done. Inline UPSERT (rather than the
        // public SetIndexMetadata) because we already hold the write lock
        // and SetIndexMetadata would re-enter it.
        using (var sentinelCmd = _connection!.CreateCommand())
        {
            sentinelCmd.CommandText = """
                INSERT INTO index_metadata (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            sentinelCmd.Parameters.AddWithValue("$key", ReverseIndexSentinelKey);
            sentinelCmd.Parameters.AddWithValue("$value",
                DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            sentinelCmd.ExecuteNonQuery();
        }
    }

}
