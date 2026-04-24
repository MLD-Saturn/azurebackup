using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- BackedUpFile -------------------------------------------------------

    /// <summary>
    /// Looks up a single backed-up-file row by local path and rebuilds its
    /// nested chunk list in <c>chunk_order</c> order. Returns <c>null</c>
    /// when no row matches.
    /// </summary>
    public BackedUpFile? GetBackedUpFile(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        // LoadChunksForFile is invoked INSIDE the lock so the per-file SELECT
        // and the per-chunk SELECT share one read-lock acquisition (RWL is
        // NoRecursion, so a nested lock would deadlock the same thread).
        return InReadLock(() =>
        {
            long id;
            BackedUpFile file;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, local_path, blob_name, file_size, last_modified,
                           file_hash, status, backed_up_at, metadata_version
                    FROM files WHERE local_path = $local_path;
                    """;
                cmd.Parameters.AddWithValue("$local_path", localPath);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return (BackedUpFile?)null;

                id = reader.GetInt64(0);
                file = new BackedUpFile
                {
                    Id = (int)id,
                    LocalPath = reader.GetString(1),
                    BlobName = reader.GetString(2),
                    FileSize = reader.GetInt64(3),
                    LastModified = ParseUtc(reader.GetString(4)),
                    FileHash = reader.GetString(5),
                    Status = (BackupStatus)reader.GetInt32(6),
                    BackedUpAt = ParseUtc(reader.GetString(7)),
                    MetadataVersion = reader.GetInt32(8),
                };
            }

            file.Chunks = LoadChunksForFile(id);
            return file;
        });
    }

    /// <summary>
    /// Returns every backed-up-file row with its chunk list populated.
    /// Two queries: one over <c>files</c>, one over <c>file_chunks</c>
    /// pre-grouped in memory by <c>file_id</c>. This avoids the N+1
    /// pattern that <see cref="GetBackedUpFile"/> uses for single rows.
    /// </summary>
    public List<BackedUpFile> GetAllBackedUpFiles()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var byId = new Dictionary<long, BackedUpFile>();
            var result = new List<BackedUpFile>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, local_path, blob_name, file_size, last_modified,
                           file_hash, status, backed_up_at, metadata_version
                    FROM files;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var file = new BackedUpFile
                    {
                        Id = (int)id,
                        LocalPath = reader.GetString(1),
                        BlobName = reader.GetString(2),
                        FileSize = reader.GetInt64(3),
                        LastModified = ParseUtc(reader.GetString(4)),
                        FileHash = reader.GetString(5),
                        Status = (BackupStatus)reader.GetInt32(6),
                        BackedUpAt = ParseUtc(reader.GetString(7)),
                        MetadataVersion = reader.GetInt32(8),
                    };
                    byId[id] = file;
                    result.Add(file);
                }
            }

            if (byId.Count == 0) return result;

            // Single pass over file_chunks; chunk_order ASC so we can append
            // directly into each file's Chunks list without sorting later.
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT file_id, chunk_index, offset, length, hash, blob_name
                    FROM file_chunks
                    ORDER BY file_id, chunk_order;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!byId.TryGetValue(reader.GetInt64(0), out var file)) continue;
                    file.Chunks.Add(new ChunkInfo
                    {
                        Index = reader.GetInt32(1),
                        Offset = reader.GetInt64(2),
                        Length = reader.GetInt32(3),
                        Hash = reader.GetString(4),
                        BlobName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    });
                }
            }

            return result;
        });
    }

    /// <summary>
    /// Inserts or updates a file row keyed by <see cref="BackedUpFile.LocalPath"/>.
    /// The nested chunk list is replaced atomically inside a single
    /// transaction; readers never observe a partial chunk list.
    /// </summary>
    public void SaveBackedUpFile(BackedUpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B22: per-file diag breadcrumb so a SQLITE_FULL / SQLITE_IOERR /
        // SQLITE_CONSTRAINT during the upsert lands in the per-file
        // .diag bundle (via FileOperationDiagnostics.RecordAmbient) AND
        // the global session log. Pre-B22 the failure surfaced only as
        // a generic SqliteException with no record of which file row
        // was being written.
        EmitDiagAmbient($"SaveBackedUpFile: enter (path={file.LocalPath}, size={file.FileSize}, chunks={file.Chunks.Count})");

        try
        {
            InWriteLock(() =>
            {
                using var tx = _connection.BeginTransaction();

        long fileId;
        using (var upsert = _connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO files (local_path, blob_name, file_size, last_modified,
                                   file_hash, status, backed_up_at, metadata_version)
                VALUES ($local_path, $blob_name, $file_size, $last_modified,
                        $file_hash, $status, $backed_up_at, $metadata_version)
                ON CONFLICT(local_path) DO UPDATE SET
                    blob_name = excluded.blob_name,
                    file_size = excluded.file_size,
                    last_modified = excluded.last_modified,
                    file_hash = excluded.file_hash,
                    status = excluded.status,
                    backed_up_at = excluded.backed_up_at,
                    metadata_version = excluded.metadata_version
                RETURNING id;
                """;
            upsert.Parameters.AddWithValue("$local_path", file.LocalPath);
            upsert.Parameters.AddWithValue("$blob_name", file.BlobName ?? string.Empty);
            upsert.Parameters.AddWithValue("$file_size", file.FileSize);
            upsert.Parameters.AddWithValue("$last_modified", FormatUtc(file.LastModified));
            upsert.Parameters.AddWithValue("$file_hash", file.FileHash ?? string.Empty);
            upsert.Parameters.AddWithValue("$status", (int)file.Status);
            upsert.Parameters.AddWithValue("$backed_up_at", FormatUtc(file.BackedUpAt));
            upsert.Parameters.AddWithValue("$metadata_version", file.MetadataVersion);
            fileId = (long)upsert.ExecuteScalar()!;
        }

        // Mirror the model id back to the caller so subsequent saves take
        // the UPDATE branch even if the caller forgot to round-trip first.
        file.Id = (int)fileId;

        // Replace the chunk list AND the matching chunk_file_refs rows. The
        // reverse index in chunk_file_refs is a denormalised projection of
        // (file_path, chunk_hash) pairs that powers GetChunkEntriesForFile;
        // in the SQLite backend SaveBackedUpFile is the canonical writer of
        // that relationship (LiteDB used to derive it from a separate
        // ReferencingFiles list on each ChunkIndexEntry, which we dropped
        // per eval doc \u00a74). Keeping both writes inside the same
        // transaction means readers never see a divergent state.
        //
        // C-5 note: ChunkIndexService.AddReference / RemoveFileReferencesAsync /
        // UpdateFileChunksAsync ALSO write chunk_file_refs (via
        // UpsertChunkFileRef and the Delete* primitives). Those mutations
        // are idempotent on the (file_path, chunk_hash, chunk_index) triple,
        // so the orchestrator's pattern of SaveBackedUpFile followed by
        // UpdateFileChunksAsync is safe even though both touch this table.
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM file_chunks WHERE file_id = $file_id;";
            clear.Parameters.AddWithValue("$file_id", fileId);
            clear.ExecuteNonQuery();
        }
        using (var clearRefs = _connection.CreateCommand())
        {
            clearRefs.Transaction = tx;
            clearRefs.CommandText = "DELETE FROM chunk_file_refs WHERE file_path = $file_path;";
            clearRefs.Parameters.AddWithValue("$file_path", file.LocalPath);
            clearRefs.ExecuteNonQuery();
        }

        if (file.Chunks.Count > 0)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO file_chunks
                    (file_id, chunk_order, chunk_index, offset, length, hash, blob_name)
                VALUES ($file_id, $chunk_order, $chunk_index, $offset, $length, $hash, $blob_name);
                """;
            var p_fileId = insert.Parameters.AddWithValue("$file_id", fileId);
            var p_order = insert.Parameters.Add("$chunk_order", SqliteType.Integer);
            var p_index = insert.Parameters.Add("$chunk_index", SqliteType.Integer);
            var p_offset = insert.Parameters.Add("$offset", SqliteType.Integer);
            var p_length = insert.Parameters.Add("$length", SqliteType.Integer);
            var p_hash = insert.Parameters.Add("$hash", SqliteType.Text);
            var p_blob = insert.Parameters.Add("$blob_name", SqliteType.Text);

            using var insertRef = _connection.CreateCommand();
            insertRef.Transaction = tx;
            insertRef.CommandText = """
                INSERT INTO chunk_file_refs
                    (file_path, chunk_hash, chunk_index, referenced_at)
                VALUES ($file_path, $chunk_hash, $chunk_index, $referenced_at);
                """;
            var refPath = insertRef.Parameters.AddWithValue("$file_path", file.LocalPath);
            var refHash = insertRef.Parameters.Add("$chunk_hash", SqliteType.Text);
            var refIndex = insertRef.Parameters.Add("$chunk_index", SqliteType.Integer);
            var refTime = insertRef.Parameters.AddWithValue("$referenced_at",
                FormatUtc(file.BackedUpAt));

            for (var i = 0; i < file.Chunks.Count; i++)
            {
                var chunk = file.Chunks[i];
                p_order.Value = i;
                p_index.Value = chunk.Index;
                p_offset.Value = chunk.Offset;
                p_length.Value = chunk.Length;
                p_hash.Value = chunk.Hash ?? string.Empty;
                p_blob.Value = (object?)chunk.BlobName ?? string.Empty;
                insert.ExecuteNonQuery();

                refHash.Value = chunk.Hash ?? string.Empty;
                refIndex.Value = chunk.Index;
                insertRef.ExecuteNonQuery();
            }

            // Document binding so the analyzer sees the params are used.
            _ = p_fileId;
            _ = refPath;
            _ = refTime;
        }

        tx.Commit();
        });
            EmitDiagAmbient($"SaveBackedUpFile: commit OK (path={file.LocalPath})");
        }
        catch (Exception ex)
        {
            EmitDiagAmbient($"SaveBackedUpFile: FAILED with {ex.GetType().Name}: {ex.Message} (path={file.LocalPath})");
            throw;
        }
    }

    /// <summary>
    /// Bulk-loads many <see cref="BackedUpFile"/> rows (with their nested
    /// chunk lists and matching chunk_file_refs rows) in a single
    /// transaction. Tens of thousands of files via the public
    /// <see cref="SaveBackedUpFile"/> would open one transaction per file
    /// and dominate setup time at C-3 scale.
    ///
    /// <para>
    /// Used by two callers in the same assembly:
    /// </para>
    /// <list type="bullet">
    ///   <item>The C-3 head-to-head benchmarks (staging test data).</item>
    ///   <item>C-2 migration in <c>LocalDatabaseService.Migration.Sqlite.cs</c>
    ///     (copying LiteDB file + chunk rows into fresh SQLite).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Not</b> exposed via <see cref="IDatabaseBackend"/> on purpose -
    /// production consumers of the backend contract always go through
    /// <see cref="SaveBackedUpFile"/>, which is the canonical writer of
    /// the (file, chunk) relationship.
    /// </para>
    /// </summary>
    internal void BulkInsertFiles(IEnumerable<BackedUpFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B22: collect once so the diag can report the row count up front.
        // The IEnumerable is materialised inside the transaction below
        // anyway; pulling it into a list here changes nothing semantically.
        var fileList = files as IList<BackedUpFile> ?? files.ToList();
        EmitDiag($"BulkInsertFiles: enter ({fileList.Count} files)");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            InWriteLock(() =>
            {
                using var tx = _connection.BeginTransaction();

            using var fileInsert = _connection.CreateCommand();
        fileInsert.Transaction = tx;
        fileInsert.CommandText = """
            INSERT INTO files (local_path, blob_name, file_size, last_modified,
                               file_hash, status, backed_up_at, metadata_version)
            VALUES ($local_path, $blob_name, $file_size, $last_modified,
                    $file_hash, $status, $backed_up_at, $metadata_version)
            RETURNING id;
            """;
        var fp_path = fileInsert.Parameters.Add("$local_path", SqliteType.Text);
        var fp_blob = fileInsert.Parameters.Add("$blob_name", SqliteType.Text);
        var fp_size = fileInsert.Parameters.Add("$file_size", SqliteType.Integer);
        var fp_modified = fileInsert.Parameters.Add("$last_modified", SqliteType.Text);
        var fp_hash = fileInsert.Parameters.Add("$file_hash", SqliteType.Text);
        var fp_status = fileInsert.Parameters.Add("$status", SqliteType.Integer);
        var fp_backed = fileInsert.Parameters.Add("$backed_up_at", SqliteType.Text);
        var fp_meta = fileInsert.Parameters.Add("$metadata_version", SqliteType.Integer);

        using var chunkInsert = _connection.CreateCommand();
        chunkInsert.Transaction = tx;
        chunkInsert.CommandText = """
            INSERT INTO file_chunks
                (file_id, chunk_order, chunk_index, offset, length, hash, blob_name)
            VALUES ($file_id, $chunk_order, $chunk_index, $offset, $length, $hash, $blob_name);
            """;
        var cp_fileId = chunkInsert.Parameters.Add("$file_id", SqliteType.Integer);
        var cp_order = chunkInsert.Parameters.Add("$chunk_order", SqliteType.Integer);
        var cp_index = chunkInsert.Parameters.Add("$chunk_index", SqliteType.Integer);
        var cp_offset = chunkInsert.Parameters.Add("$offset", SqliteType.Integer);
        var cp_length = chunkInsert.Parameters.Add("$length", SqliteType.Integer);
        var cp_hash = chunkInsert.Parameters.Add("$hash", SqliteType.Text);
        var cp_blob = chunkInsert.Parameters.Add("$blob_name", SqliteType.Text);

        using var refInsert = _connection.CreateCommand();
        refInsert.Transaction = tx;
        refInsert.CommandText = """
            INSERT INTO chunk_file_refs
                (file_path, chunk_hash, chunk_index, referenced_at)
            VALUES ($file_path, $chunk_hash, $chunk_index, $referenced_at);
            """;
        var rp_path = refInsert.Parameters.Add("$file_path", SqliteType.Text);
        var rp_hash = refInsert.Parameters.Add("$chunk_hash", SqliteType.Text);
        var rp_index = refInsert.Parameters.Add("$chunk_index", SqliteType.Integer);
        var rp_referenced = refInsert.Parameters.Add("$referenced_at", SqliteType.Text);

        foreach (var file in fileList)
        {
            fp_path.Value = file.LocalPath;
            fp_blob.Value = file.BlobName ?? string.Empty;
            fp_size.Value = file.FileSize;
            fp_modified.Value = FormatUtc(file.LastModified);
            fp_hash.Value = file.FileHash ?? string.Empty;
            fp_status.Value = (int)file.Status;
            fp_backed.Value = FormatUtc(file.BackedUpAt);
            fp_meta.Value = file.MetadataVersion;

            var fileId = (long)fileInsert.ExecuteScalar()!;
            cp_fileId.Value = fileId;
            rp_path.Value = file.LocalPath;
            rp_referenced.Value = FormatUtc(file.BackedUpAt);

            for (var i = 0; i < file.Chunks.Count; i++)
            {
                var chunk = file.Chunks[i];
                cp_order.Value = i;
                cp_index.Value = chunk.Index;
                cp_offset.Value = chunk.Offset;
                cp_length.Value = chunk.Length;
                cp_hash.Value = chunk.Hash ?? string.Empty;
                cp_blob.Value = (object?)chunk.BlobName ?? string.Empty;
                chunkInsert.ExecuteNonQuery();

                rp_hash.Value = chunk.Hash ?? string.Empty;
                rp_index.Value = chunk.Index;
                refInsert.ExecuteNonQuery();
            }
        }

        tx.Commit();
        });
            EmitDiag($"BulkInsertFiles: commit OK ({fileList.Count} files in {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            EmitDiag($"BulkInsertFiles: FAILED with {ex.GetType().Name}: {ex.Message} after {sw.ElapsedMilliseconds} ms ({fileList.Count} files)");
            throw;
        }
    }

    /// <summary>
    /// Benchmark-only: drops every row from <c>chunk_file_refs</c> and
    /// clears the <c>ReverseIndexBuiltAt</c> sentinel so a subsequent
    /// <see cref="RebuildReverseChunkIndex"/> call has work to do.
    /// Used by the C-3 (3/N) head-to-head where the SQLite leg is seeded
    /// via <see cref="BulkInsertFiles"/> (which writes
    /// chunk_file_refs as a side-effect) and we need to wipe the reverse
    /// index between iterations to measure the rebuild cost in isolation.
    ///
    /// <para>
    /// <b>Not</b> exposed via <see cref="IDatabaseBackend"/>: production
    /// code never wants to drop the reverse index without rebuilding it.
    /// </para>
    /// </summary>
    internal void ClearReverseChunkIndexForBenchmark()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using (var tx = _connection.BeginTransaction())
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM chunk_file_refs;";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    // Match the constant ReverseIndexSentinelKey used by
                    // RebuildReverseChunkIndex / IsReverseChunkIndexBuilt.
                    cmd.CommandText = "DELETE FROM index_metadata WHERE key = 'ReverseIndexBuiltAt';";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // C-3 (3c-3) - BI2: explicit WAL checkpoint after the wipe so
            // the subsequent rebuild benchmark starts from a CLEAN WAL state.
            // Without this, the DELETE-of-N-rows above leaves dirty WAL pages
            // that the rebuild measurement would then have to compete with -
            // adding noise that biases the SQLite numbers downward.
            // TRUNCATE leaves the WAL file zero-sized.
            using (var checkpointCmd = _connection.CreateCommand())
            {
                checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpointCmd.ExecuteNonQuery();
            }
        });
    }

}
