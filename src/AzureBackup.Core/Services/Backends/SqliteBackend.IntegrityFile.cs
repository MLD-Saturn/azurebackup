using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B44: surface for verifying the on-disk SQLite/SQLCipher database file
/// itself (NOT the user-visible Data Integrity feature, which checks
/// backed-up files against Azure storage).
/// </summary>
internal sealed partial class SqliteBackend
{
    /// <summary>
    /// Runs <c>PRAGMA cipher_integrity_check</c> followed by
    /// <c>PRAGMA integrity_check</c> against the open connection and
    /// returns a structured result. See
    /// <see cref="DatabaseFileIntegrityResult"/> for the meaning of
    /// each pragma's output.
    /// </summary>
    public DatabaseFileIntegrityResult CheckDatabaseFileIntegrity()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        EmitDiag("CheckDatabaseFileIntegrity: enter");

        // Take the WRITE lock for the duration. Both pragmas can take
        // many seconds on a large database and they walk every page;
        // letting a concurrent writer run alongside would either
        // serialize behind us anyway or, worse, mutate the page
        // images mid-scan and produce a false positive.
        return InWriteLock(() =>
        {
            var cipherMessages = RunPragmaIntegrityCheck("cipher_integrity_check");
            var sqliteMessages = RunPragmaIntegrityCheck("integrity_check");
            var result = new DatabaseFileIntegrityResult(
                CipherIntegrityMessages: cipherMessages,
                SqliteIntegrityMessages: sqliteMessages);
            EmitDiag(
                $"CheckDatabaseFileIntegrity: complete (cipherOk={result.CipherOk}, sqliteOk={result.SqliteOk}, " +
                $"cipherRows={cipherMessages.Count}, sqliteRows={sqliteMessages.Count})");
            return result;
        });
    }

    private List<string> RunPragmaIntegrityCheck(string pragmaName)
    {
        var messages = new List<string>();
        using var cmd = _connection!.CreateCommand();
        // PRAGMA names cannot be parameterised; the only inputs are
        // hard-coded literals on the call site so the SQL string is
        // safe by construction.
        cmd.CommandText = $"PRAGMA {pragmaName};";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                messages.Add(reader.GetString(0));
            }
        }
        catch (SqliteException ex)
        {
            // Pragmas can themselves fail when the corruption is bad
            // enough that the engine cannot even read the page that
            // would describe the result row. Surface this as a
            // synthetic failure message so the caller sees a clear
            // signal rather than an empty list that looks like "ok".
            messages.Add($"pragma {pragmaName} failed: SQLite Error {ex.SqliteErrorCode}: {ex.Message}");
        }
        return messages;
    }
}

/// <summary>
/// Structured result of <see cref="SqliteBackend.CheckDatabaseFileIntegrity"/>.
///
/// <para>
/// <c>cipher_integrity_check</c> (SQLCipher) verifies the HMAC of every
/// page on disk. <b>It returns zero rows when every page verifies and
/// one row per failure otherwise</b> -- this is the opposite of stock
/// SQLite's <c>integrity_check</c> and the most common pitfall when
/// consuming its output. Failures here mean the ciphertext was
/// truncated or bit-flipped after it was written, OR the wrong
/// SQLCipher parameters are in use (e.g. a forced page-size
/// mismatch).
/// </para>
///
/// <para>
/// <c>integrity_check</c> (stock SQLite) walks the b-tree and reports
/// rowid / page-link / index-row inconsistencies. It returns a single
/// row containing the literal <c>"ok"</c> when the database is healthy
/// or one row per problem otherwise. Failures here mean the
/// <i>plaintext</i> image is malformed even though every page decrypted
/// cleanly -- the symptom that surfaces as SQLite error 11
/// ("database disk image is malformed") on the next write attempt.
/// </para>
/// </summary>
/// <param name="CipherIntegrityMessages">
/// Rows returned by <c>PRAGMA cipher_integrity_check</c>. An empty
/// list means every page HMAC verified.
/// </param>
/// <param name="SqliteIntegrityMessages">
/// Rows returned by <c>PRAGMA integrity_check</c>. A single <c>"ok"</c>
/// row means the b-tree is structurally valid.
/// </param>
public sealed record DatabaseFileIntegrityResult(
    IReadOnlyList<string> CipherIntegrityMessages,
    IReadOnlyList<string> SqliteIntegrityMessages)
{
    public bool CipherOk => CipherIntegrityMessages.Count == 0;

    public bool SqliteOk =>
        SqliteIntegrityMessages.Count == 1 &&
        string.Equals(SqliteIntegrityMessages[0], "ok", StringComparison.Ordinal);

    public bool IsHealthy => CipherOk && SqliteOk;
}

