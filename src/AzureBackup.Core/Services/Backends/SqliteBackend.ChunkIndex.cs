using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- ChunkIndex ---------------------------------------------------------

    /// <summary>
    /// Looks up a single chunk row by hash. The returned object's
    /// ReferencingFiles list is intentionally left empty - the reverse
    /// index in <c>chunk_file_refs</c> is the authoritative source for
    /// that data and is queried separately when callers need it.
    /// </summary>
    public ChunkIndexEntry? GetChunkIndexEntry(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_hash, first_uploaded_at, original_uploader_path,
                       size_bytes, reference_count, current_tier, last_verified_at
                FROM chunk_index WHERE chunk_hash = $chunk_hash;
                """;
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (ChunkIndexEntry?)null;
            return ReadChunkEntry(reader);
        });
    }

    /// <summary>
    /// Inserts or updates a single chunk row. Matches LiteDB upsert semantics.
    /// </summary>
    public void SaveChunkIndexEntry(ChunkIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = ChunkIndexUpsertSql;
            BindChunkEntry(cmd, entry);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// D6: stamps the expected encrypted-blob MD5 on a chunk_index row.
    /// Called from <see cref="IBlobStorageService"/> upload paths once the
    /// MD5 is known (we compute it locally rather than relying on Azure's
    /// returned ContentHash so the value is identical for in-memory and
    /// real backends). Pre-D6 chunks remain null until a re-upload OR the
    /// next rebuild pass populates them.
    /// </summary>
    /// <remarks>
    /// We deliberately update ONLY this column rather than upserting the
    /// whole row -- the upload path doesn't have the file count / tier
    /// information needed for the full row, and we don't want a partial
    /// upsert wiping reference counts. When the chunk_index row does NOT
    /// yet exist (the upload-time callback runs BEFORE
    /// <see cref="ChunkIndexService.AddReference"/> creates the row) we
    /// insert a minimal placeholder carrying ONLY the MD5 -- the
    /// <see cref="ChunkIndexUpsertSql"/> clause is careful to NOT touch
    /// expected_encrypted_md5 on conflict, so the subsequent upsert
    /// fills in the other fields without clobbering the MD5.
    /// </remarks>
    public void SetChunkExpectedMd5(string chunkHash, byte[] md5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        ArgumentNullException.ThrowIfNull(md5);
        if (md5.Length != 16)
            throw new ArgumentException("Expected 16-byte MD5", nameof(md5));
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            // Insert-or-update keyed on chunk_hash. The placeholder row's
            // size/refcount/tier defaults are sentinel values that the
            // subsequent SaveChunkIndexEntry overwrites; the MD5 we set
            // here is preserved because ChunkIndexUpsertSql's ON CONFLICT
            // clause omits expected_encrypted_md5 from its excluded list.
            cmd.CommandText = """
                INSERT INTO chunk_index
                    (chunk_hash, first_uploaded_at, original_uploader_path,
                     size_bytes, reference_count, current_tier, last_verified_at,
                     expected_encrypted_md5)
                VALUES
                    ($hash, $now, '', 0, 0, 0, $now, $md5)
                ON CONFLICT(chunk_hash) DO UPDATE SET
                    expected_encrypted_md5 = excluded.expected_encrypted_md5;
                """;
            cmd.Parameters.AddWithValue("$hash", chunkHash);
            cmd.Parameters.AddWithValue("$md5", md5);
            cmd.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// D6: read the persisted upload-time MD5 for a chunk. Returns null when
    /// the chunk has no row, the row pre-dates D6, or the chunk was uploaded
    /// before the column existed and never re-stamped. The integrity-check
    /// engine treats null as "T1 cannot decide -- pass" so legacy chunks
    /// degrade gracefully.
    /// </summary>
    public byte[]? GetChunkExpectedMd5(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT expected_encrypted_md5 FROM chunk_index WHERE chunk_hash = $hash;";
            cmd.Parameters.AddWithValue("$hash", chunkHash);
            var result = cmd.ExecuteScalar();
            return result is byte[] bytes ? bytes : null;
        });
    }

    /// <summary>
    /// D10: returns chunk hashes whose <c>expected_encrypted_md5</c>
    /// column is null. Used by the legacy-chunk backfill scan to
    /// promote chunks uploaded before D6 by running T2 download +
    /// verify and stamping the MD5 only when the envelope checks out.
    /// Returns an enumerable rather than a list so the caller can
    /// stream very large result sets (a multi-million-chunk corpus).
    /// </summary>
    public IEnumerable<string> GetChunkHashesWithNullExpectedMd5()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        // Buffer the result up front so the read lock is released before the
        // caller does network I/O per chunk -- otherwise a subsequent
        // SetChunkExpectedMd5 (writer) would block waiting for this reader.
        return InReadLock<IEnumerable<string>>(() =>
        {
            var hashes = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT chunk_hash FROM chunk_index WHERE expected_encrypted_md5 IS NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        });
    }

    /// <summary>
    /// D10: count of chunks awaiting MD5 backfill. Cheap COUNT(*) so
    /// the UI can show the scope of work before the scan starts.
    /// </summary>
    public long CountChunksWithNullExpectedMd5()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunk_index WHERE expected_encrypted_md5 IS NULL;";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        });
    }

    /// <summary>
    /// Bulk upsert in a single transaction with a reused prepared statement.
    /// At the reverse-index rebuild scale (500K chunks measured in Phase 5)
    /// this is dramatically faster than N round-trips - SQLite's bulk
    /// throughput is ~50K inserts/second per transaction so the worst case
    /// is ~10 s of pure DB time.
    /// </summary>
    public void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B22: materialise so the diag can report row count + per-row failures
        var entryList = entries as IList<ChunkIndexEntry> ?? entries.ToList();
        EmitDiag($"BulkInsertChunkIndexEntries: enter ({entryList.Count} entries)");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            InWriteLock(() =>
            {
                using var tx = _connection.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = ChunkIndexUpsertSql;

                // Pre-add parameters so we can rebind values per row without
                // re-creating SqliteParameter instances.
                var p_hash = cmd.Parameters.Add("$chunk_hash", SqliteType.Text);
                var p_uploaded = cmd.Parameters.Add("$first_uploaded_at", SqliteType.Text);
                var p_uploader = cmd.Parameters.Add("$original_uploader_path", SqliteType.Text);
                var p_size = cmd.Parameters.Add("$size_bytes", SqliteType.Integer);
                var p_refcount = cmd.Parameters.Add("$reference_count", SqliteType.Integer);
                var p_tier = cmd.Parameters.Add("$current_tier", SqliteType.Integer);
                var p_verified = cmd.Parameters.Add("$last_verified_at", SqliteType.Text);

                foreach (var e in entryList)
                {
                    p_hash.Value = e.ChunkHash;
                    p_uploaded.Value = FormatUtc(e.FirstUploadedAt);
                    p_uploader.Value = e.OriginalUploaderPath ?? string.Empty;
                    p_size.Value = e.SizeBytes;
                    p_refcount.Value = e.ReferenceCount;
                    p_tier.Value = (int)e.CurrentTier;
                    p_verified.Value = FormatUtc(e.LastVerifiedAt);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            });
            EmitDiag($"BulkInsertChunkIndexEntries: commit OK ({entryList.Count} entries in {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            EmitDiag($"BulkInsertChunkIndexEntries: FAILED with {ex.GetType().Name}: {ex.Message} after {sw.ElapsedMilliseconds} ms ({entryList.Count} entries)");
            throw;
        }
    }

    /// <summary>
    /// Deletes one chunk row by hash. No-op when the row is absent (matches
    /// LiteDB's <c>Delete</c> contract).
    /// </summary>
    public void DeleteChunkIndexEntry(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chunk_index WHERE chunk_hash = $chunk_hash;";
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns every chunk row in primary-key order.
    /// </summary>
    public List<ChunkIndexEntry> GetAllChunkIndexEntries()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<ChunkIndexEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_hash, first_uploaded_at, original_uploader_path,
                       size_bytes, reference_count, current_tier, last_verified_at
                FROM chunk_index;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadChunkEntry(reader));
            }
            return result;
        });
    }

    /// <summary>
    /// Projects every chunk row to its (refcount, size, tier) tuple.
    /// Avoids materialising full ChunkIndexEntry instances on the hot
    /// statistics-summary path; replaces the LiteDB equivalent that
    /// deserialised the entire BSON document just to read three fields.
    /// </summary>
    public Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)>
        GetChunkIndexSummaryMap()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new Dictionary<string, (int, long, StorageTier)>(StringComparer.Ordinal);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_hash, reference_count, size_bytes, current_tier
                FROM chunk_index;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = (
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    (StorageTier)reader.GetInt32(3));
            }
            return result;
        });
    }

    /// <summary>
    /// Cheap row count of chunk_index. SQLite resolves this in O(1) for
    /// small databases via the page count stat; on larger DBs it scans
    /// the index but never the heap.
    /// </summary>
    public int GetChunkIndexCount()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM chunk_index;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
    }

    /// <summary>
    /// Truncates both chunk_index and chunk_file_refs in a single
    /// transaction so a partial wipe is never observable. Used by the
    /// "reset chunk index" maintenance command and by integration tests
    /// that need a clean slate per run.
    /// </summary>
    public void ClearChunkIndex()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM chunk_index;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM chunk_file_refs;";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
    }

    /// <summary>
    /// Returns every chunk with reference_count = 0. Uses the
    /// <c>idx_chunk_index_refcount</c> partial seek so the cost is O(orphans)
    /// rather than O(all chunks).
    /// </summary>
    public List<ChunkIndexEntry> GetOrphanedChunks()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<ChunkIndexEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT chunk_hash, first_uploaded_at, original_uploader_path,
                       size_bytes, reference_count, current_tier, last_verified_at
                FROM chunk_index WHERE reference_count = 0;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadChunkEntry(reader));
            }
            return result;
        });
    }

    /// <summary>
    /// Authoritative reference count for <paramref name="chunkHash"/>:
    /// <c>SELECT COUNT(*) FROM chunk_file_refs WHERE chunk_hash = ?</c>.
    /// Used by <c>ChunkIndexService</c> to maintain
    /// <see cref="ChunkIndexEntry.ReferenceCount"/> without depending on
    /// the in-memory <see cref="ChunkIndexEntry.ReferencingFiles"/> list
    /// (which the SQLite GetChunkIndexEntry leaves empty by design).
    /// </summary>
    public int GetReferenceCountForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunk_file_refs WHERE chunk_hash = $chunk_hash;";
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
    }

    /// <summary>
    /// Returns every (file_path, chunk_index, referenced_at) triple
    /// for <paramref name="chunkHash"/> from the canonical
    /// <c>chunk_file_refs</c> reverse-index table. Used by
    /// <c>ChunkIndexService.AddReference</c> to detect duplicates
    /// without depending on the in-memory <c>ReferencingFiles</c> list.
    /// </summary>
    public List<ChunkFileReference> GetReferencingFilesForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var result = new List<ChunkFileReference>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT file_path, chunk_index, referenced_at
                FROM chunk_file_refs
                WHERE chunk_hash = $chunk_hash;
                """;
            cmd.Parameters.AddWithValue("$chunk_hash", chunkHash);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChunkFileReference
                {
                    FilePath = reader.GetString(0),
                    ChunkIndex = reader.GetInt32(1),
                    ReferencedAt = ParseUtc(reader.GetString(2)),
                });
            }
            return result;
        });
    }

    // Shared SQL string and reader helper to keep the DML and projection
    // logic in one place; every chunk_index code path reads the same
    // column ordering.
    private const string ChunkIndexUpsertSql = """
        INSERT INTO chunk_index
            (chunk_hash, first_uploaded_at, original_uploader_path,
             size_bytes, reference_count, current_tier, last_verified_at)
        VALUES
            ($chunk_hash, $first_uploaded_at, $original_uploader_path,
             $size_bytes, $reference_count, $current_tier, $last_verified_at)
        ON CONFLICT(chunk_hash) DO UPDATE SET
            first_uploaded_at = excluded.first_uploaded_at,
            original_uploader_path = excluded.original_uploader_path,
            size_bytes = excluded.size_bytes,
            reference_count = excluded.reference_count,
            current_tier = excluded.current_tier,
            last_verified_at = excluded.last_verified_at;
        """;

    private static void BindChunkEntry(SqliteCommand cmd, ChunkIndexEntry entry)
    {
        cmd.Parameters.AddWithValue("$chunk_hash", entry.ChunkHash);
        cmd.Parameters.AddWithValue("$first_uploaded_at", FormatUtc(entry.FirstUploadedAt));
        cmd.Parameters.AddWithValue("$original_uploader_path",
            entry.OriginalUploaderPath ?? string.Empty);
        cmd.Parameters.AddWithValue("$size_bytes", entry.SizeBytes);
        cmd.Parameters.AddWithValue("$reference_count", entry.ReferenceCount);
        cmd.Parameters.AddWithValue("$current_tier", (int)entry.CurrentTier);
        cmd.Parameters.AddWithValue("$last_verified_at", FormatUtc(entry.LastVerifiedAt));
    }

    private static ChunkIndexEntry ReadChunkEntry(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new()
        {
            ChunkHash = reader.GetString(0),
            FirstUploadedAt = ParseUtc(reader.GetString(1)),
            OriginalUploaderPath = reader.GetString(2),
            SizeBytes = reader.GetInt64(3),
            ReferenceCount = reader.GetInt32(4),
            CurrentTier = (StorageTier)reader.GetInt32(5),
            LastVerifiedAt = ParseUtc(reader.GetString(6)),
        };

}
