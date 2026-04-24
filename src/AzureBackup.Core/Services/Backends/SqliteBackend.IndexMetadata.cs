using AzureBackup.Core.Models;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

internal sealed partial class SqliteBackend
{

    // ---- IndexMetadata ------------------------------------------------------

    /// <summary>
    /// Reads a timestamp value from the <c>index_metadata</c> key/value table.
    /// Values are persisted as ISO-8601 strings (round-trippable, sortable,
    /// and human-readable when poking at the DB with the sqlite3 CLI).
    /// </summary>
    public DateTime? GetIndexMetadata(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM index_metadata WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var raw = cmd.ExecuteScalar() as string;
            if (raw == null) return (DateTime?)null;

            // Round-trip via O so DateTimeKind.Utc survives. We always write UTC
            // values in SetIndexMetadata so this is safe.
            return DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
        });
    }

    /// <summary>
    /// Upserts a timestamp value under <paramref name="key"/>.
    /// Always normalises to UTC before persisting so reads via
    /// <see cref="GetIndexMetadata"/> are deterministic regardless of the
    /// caller's local time zone.
    /// </summary>
    public void SetIndexMetadata(string key, DateTime value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        InWriteLock(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO index_metadata (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value",
                utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns every (key, value) row in the <c>index_metadata</c> table.
    /// Implements <see cref="IDatabaseBackend.GetAllIndexMetadata"/> for the
    /// SQLite backend. Used by C-2 migration; production consumers have no
    /// need to enumerate metadata because they know their keys up front.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> GetAllIndexMetadata()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        // B23: serialize against the shared SqliteConnection -- see InReadLock comment.
        return InReadLock<IReadOnlyDictionary<string, DateTime>>(() =>
        {
            var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM index_metadata;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var raw = reader.GetString(1);
                // Stored as ISO-8601 "O" format per SetIndexMetadata.
                if (DateTime.TryParse(raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
                {
                    result[key] = parsed;
                }
                // Silently skip unparseable rows. In practice this is never
                // hit because SetIndexMetadata is the only writer; if a
                // foreign tool ever writes here we prefer not to crash.
            }
            return result;
        });
    }

}
