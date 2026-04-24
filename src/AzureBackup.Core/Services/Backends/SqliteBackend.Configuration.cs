using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- Configuration ------------------------------------------------------

    // Discriminator values for watched_folder_excludes.kind. Must stay in
    // sync with SaveConfigurationCore / GetConfiguration.
    private const int ExcludeKindPattern = 0;     // WatchedFolder.ExcludePatterns
    private const int ExcludeKindSubfolder = 1;   // WatchedFolder.ExcludeSubfolders

    /// <summary>
    /// Reads the singleton config row (always row id 1) and rebuilds the
    /// nested <see cref="WatchedFolder"/> and global-exclude lists from
    /// their relational tables. Returns a default-constructed
    /// <see cref="BackupConfiguration"/> if the row was never saved (the
    /// row exists from the schema seed but every column is at its default).
    /// </summary>
    public BackupConfiguration GetConfiguration()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            var config = new BackupConfiguration();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT auth_method, storage_account_name, encrypted_connection_string,
                           container_name, password_salt, password_verification_hash,
                           last_backup_time, total_bytes_uploaded,
                           failed_login_attempts, lockout_until_ticks,
                           is_entra_id_authenticated, entra_id_user_name,
                           config_version, memory_limit_enabled, memory_limit_mb
                    FROM config WHERE id = 1;
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    config.AuthMethod = (AzureAuthMethod)reader.GetInt32(0);
                    config.StorageAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    config.EncryptedConnectionString = reader.IsDBNull(2)
                        ? null
                        : (byte[])reader.GetValue(2);
                    config.ContainerName = reader.IsDBNull(3) ? null : reader.GetString(3);
                    config.PasswordSalt = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
                    config.PasswordVerificationHash = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
                    config.LastBackupTime = reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6));
                    config.TotalBytesUploaded = reader.GetInt64(7);
                    config.FailedLoginAttempts = reader.GetInt32(8);
                    config.LockoutUntilTicks = reader.IsDBNull(9) ? null : reader.GetInt64(9);
                    config.IsEntraIdAuthenticated = reader.GetInt32(10) != 0;
                    config.EntraIdUserName = reader.IsDBNull(11) ? null : reader.GetString(11);
                    config.ConfigVersion = reader.GetInt32(12);
                    config.MemoryLimitEnabled = reader.GetInt32(13) != 0;
                    config.MemoryLimitMB = reader.GetInt32(14);
                }
            }

            // Watched folders (and their per-folder exclude lists).
            var foldersById = new Dictionary<long, WatchedFolder>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, path, storage_tier, is_enabled FROM watched_folders ORDER BY id;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var folder = new WatchedFolder
                    {
                        Path = reader.GetString(1),
                        StorageTier = (StorageTier)reader.GetInt32(2),
                        IsEnabled = reader.GetInt32(3) != 0,
                    };
                    foldersById[reader.GetInt64(0)] = folder;
                    config.WatchedFolders.Add(folder);
                }
            }

            if (foldersById.Count > 0)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT folder_id, pattern, kind FROM watched_folder_excludes ORDER BY id;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!foldersById.TryGetValue(reader.GetInt64(0), out var folder)) continue;
                    var pattern = reader.GetString(1);
                    var kind = reader.GetInt32(2);
                    if (kind == ExcludeKindPattern) folder.ExcludePatterns.Add(pattern);
                    else if (kind == ExcludeKindSubfolder) folder.ExcludeSubfolders.Add(pattern);
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT pattern FROM global_exclude_patterns ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    config.GlobalExcludePatterns.Add(reader.GetString(0));
                }
            }

            return config;
        });
    }

    /// <summary>
    /// Persists the singleton config row + the nested folder / pattern lists
    /// in a single transaction. Existing rows in the child tables are
    /// dropped and re-inserted (this matches LiteDB's atomic upsert
    /// semantics; the row counts here are tiny - typically &lt;20 folders
    /// and a handful of patterns - so the delete-then-insert cost is in the
    /// microseconds).
    /// </summary>
    public void SaveConfiguration(BackupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() =>
        {
            using var tx = _connection.BeginTransaction();

        // --- config row ---------------------------------------------------
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE config SET
                    auth_method = $auth_method,
                    storage_account_name = $storage_account_name,
                    encrypted_connection_string = $encrypted_connection_string,
                    container_name = $container_name,
                    password_salt = $password_salt,
                    password_verification_hash = $password_verification_hash,
                    last_backup_time = $last_backup_time,
                    total_bytes_uploaded = $total_bytes_uploaded,
                    failed_login_attempts = $failed_login_attempts,
                    lockout_until_ticks = $lockout_until_ticks,
                    is_entra_id_authenticated = $is_entra_id_authenticated,
                    entra_id_user_name = $entra_id_user_name,
                    config_version = $config_version,
                    memory_limit_enabled = $memory_limit_enabled,
                    memory_limit_mb = $memory_limit_mb
                WHERE id = 1;
                """;
            cmd.Parameters.AddWithValue("$auth_method", (int)configuration.AuthMethod);
            cmd.Parameters.AddWithValue("$storage_account_name",
                (object?)configuration.StorageAccountName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$encrypted_connection_string",
                (object?)configuration.EncryptedConnectionString ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$container_name",
                (object?)configuration.ContainerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$password_salt",
                (object?)configuration.PasswordSalt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$password_verification_hash",
                (object?)configuration.PasswordVerificationHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_backup_time",
                configuration.LastBackupTime.HasValue
                    ? FormatUtc(configuration.LastBackupTime.Value)
                    : DBNull.Value);
            cmd.Parameters.AddWithValue("$total_bytes_uploaded", configuration.TotalBytesUploaded);
            cmd.Parameters.AddWithValue("$failed_login_attempts", configuration.FailedLoginAttempts);
            cmd.Parameters.AddWithValue("$lockout_until_ticks",
                (object?)configuration.LockoutUntilTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_entra_id_authenticated",
                configuration.IsEntraIdAuthenticated ? 1 : 0);
            cmd.Parameters.AddWithValue("$entra_id_user_name",
                (object?)configuration.EntraIdUserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$config_version", configuration.ConfigVersion);
            cmd.Parameters.AddWithValue("$memory_limit_enabled",
                configuration.MemoryLimitEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$memory_limit_mb", configuration.MemoryLimitMB);
            cmd.ExecuteNonQuery();
        }

        // --- watched folders (+ exclude / subfolder lists) ----------------
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            // ON DELETE CASCADE on watched_folder_excludes drops the child
            // rows automatically; we still need an explicit clear for
            // global_exclude_patterns below.
            clear.CommandText = "DELETE FROM watched_folders;";
            clear.ExecuteNonQuery();
        }

        foreach (var folder in configuration.WatchedFolders)
        {
            long folderId;
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO watched_folders (path, storage_tier, is_enabled)
                    VALUES ($path, $storage_tier, $is_enabled)
                    RETURNING id;
                    """;
                insert.Parameters.AddWithValue("$path", folder.Path);
                insert.Parameters.AddWithValue("$storage_tier", (int)folder.StorageTier);
                insert.Parameters.AddWithValue("$is_enabled", folder.IsEnabled ? 1 : 0);
                folderId = (long)insert.ExecuteScalar()!;
            }

            InsertExcludes(tx, folderId, folder.ExcludePatterns, ExcludeKindPattern);
            InsertExcludes(tx, folderId, folder.ExcludeSubfolders, ExcludeKindSubfolder);
        }

        // --- global excludes ----------------------------------------------
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM global_exclude_patterns;";
            clear.ExecuteNonQuery();
        }

        foreach (var pattern in configuration.GlobalExcludePatterns)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO global_exclude_patterns (pattern) VALUES ($pattern);";
            insert.Parameters.AddWithValue("$pattern", pattern);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        });
    }

    private void InsertExcludes(SqliteTransaction tx, long folderId,
        IReadOnlyList<string> patterns, int kind)
    {
        if (patterns.Count == 0) return;
        if (_connection == null) return;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO watched_folder_excludes (folder_id, pattern, kind)
            VALUES ($folder_id, $pattern, $kind);
            """;
        var folderParam = cmd.Parameters.AddWithValue("$folder_id", folderId);
        var patternParam = cmd.Parameters.Add("$pattern", SqliteType.Text);
        var kindParam = cmd.Parameters.AddWithValue("$kind", kind);

        foreach (var pattern in patterns)
        {
            patternParam.Value = pattern;
            cmd.ExecuteNonQuery();
        }

        // Avoid analyzer warning about unused params; reading them is a
        // no-op but documents that they are bound on the prepared statement.
        _ = folderParam;
        _ = kindParam;
    }

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

}
