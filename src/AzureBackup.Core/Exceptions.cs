namespace AzureBackup.Core;

/// <summary>
/// Exception thrown when data integrity verification fails.
/// This indicates potential corruption, tampering, or incorrect password.
/// </summary>
public class DataIntegrityException : Exception
{
    public string? AffectedResource { get; }

    public DataIntegrityException(string message) : base(message)
    {
    }

    public DataIntegrityException(string message, string affectedResource) : base(message)
    {
        AffectedResource = affectedResource;
    }

    public DataIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DataIntegrityException(string message, string affectedResource, Exception innerException) 
        : base(message, innerException)
    {
        AffectedResource = affectedResource;
    }
}

/// <summary>
/// Exception thrown when security policy is violated.
/// </summary>
public class SecurityPolicyException : Exception
{
    public SecurityPolicyType PolicyType { get; }

    public SecurityPolicyException(string message, SecurityPolicyType policyType) : base(message)
    {
        PolicyType = policyType;
    }

    public SecurityPolicyException(string message, SecurityPolicyType policyType, Exception innerException) 
        : base(message, innerException)
    {
        PolicyType = policyType;
    }
}

public enum SecurityPolicyType
{
    RateLimitExceeded,
    AccountLocked,
    InvalidCredentials,
    InvalidBlobName,
    TamperingDetected,
    WeakPassword
}

/// <summary>
/// Exception thrown when an invalid password is provided for the encrypted database.
/// </summary>
public class InvalidPasswordException : Exception
{
    public InvalidPasswordException(string message) : base(message)
    {
    }

    public InvalidPasswordException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// B11: Argon2id key derivation could not allocate its working memory
/// even after stepping the parallelism down to 1. Distinct from
/// <see cref="InvalidPasswordException"/> on purpose -- the user cannot
/// fix this by retyping the password; they need to free RAM or
/// restart. The unlock UI surfaces the inner OOM details so a tester
/// can quote them when reporting environment-specific failures.
/// </summary>
public class InsufficientMemoryForKdfException : Exception
{
    public InsufficientMemoryForKdfException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when Azure rejects an operation because of authentication or
/// authorization failure (HTTP 401 / 403, or an <c>AuthenticationFailedException</c>
/// from the Azure SDK). Carries the original Azure error for diagnostics so callers
/// can decide whether to invalidate cached credentials and prompt the user to re-auth.
/// </summary>
public class AzureAuthenticationException : Exception
{
    /// <summary>HTTP status code reported by Azure, if available (0 when not HTTP-based).</summary>
    public int Status { get; }

    /// <summary>Azure error code (e.g. "AuthenticationFailed"), if available.</summary>
    public string? ErrorCode { get; }

    public AzureAuthenticationException(string message, int status, string? errorCode, Exception innerException)
        : base(message, innerException)
    {
        Status = status;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Exception thrown when a hash collision is detected during deduplication verification.
/// This is an extremely rare event (SHA-256 collision probability is 2^-128) and likely
/// indicates data corruption, a bug, or an intentional attack rather than a true collision.
/// </summary>
public class HashCollisionException : Exception
{
    /// <summary>
    /// The hash value that matched but with different data.
    /// </summary>
    public string ChunkHash { get; }

    /// <summary>
    /// Size of the expected data in bytes.
    /// </summary>
    public long ExpectedSize { get; }

    /// <summary>
    /// Size of the stored data in bytes.
    /// </summary>
    public long StoredSize { get; }

    public HashCollisionException(string chunkHash, long expectedSize, long storedSize)
        : base($"CRITICAL: Hash collision detected for chunk {chunkHash}. " +
               $"Expected {expectedSize} bytes but stored chunk has {storedSize} bytes. " +
               "This may indicate data corruption or tampering.")
    {
        ChunkHash = chunkHash;
        ExpectedSize = expectedSize;
        StoredSize = storedSize;
    }

    public HashCollisionException(string chunkHash, string details)
        : base($"CRITICAL: Hash collision detected for chunk {chunkHash}. {details}")
    {
        ChunkHash = chunkHash;
    }
}

/// <summary>
/// B44: thrown when an operation against the local SQLite/SQLCipher
/// catalog file fails because the file itself is corrupted on disk
/// (SQLite error 11, <c>SQLITE_CORRUPT</c>, "database disk image is
/// malformed", or SQLite error 26, <c>SQLITE_NOTADB</c>, raised on a
/// page fetch rather than at PRAGMA-key time).
///
/// <para>
/// Distinct from <see cref="DataIntegrityException"/> on purpose:
/// <see cref="DataIntegrityException"/> describes the user's backed-up
/// files failing verification against Azure storage, whereas this
/// exception describes the local catalog file itself being unreadable.
/// The repair paths are completely different -- the user-visible Data
/// Integrity feature cannot fix this; the user must run the
/// "Verify Database File" diagnostic on the Storage Health tab and
/// follow its guidance (typically: restore the catalog from a backup
/// or rebuild it from Azure metadata).
/// </para>
/// </summary>
public class DatabaseFileCorruptException : Exception
{
    /// <summary>
    /// Underlying SQLite error code (e.g. 11 for SQLITE_CORRUPT,
    /// 26 for SQLITE_NOTADB) when the cause was a SqliteException;
    /// 0 otherwise.
    /// </summary>
    public int SqliteErrorCode { get; }

    public DatabaseFileCorruptException(string message, int sqliteErrorCode, Exception innerException)
        : base(message, innerException)
    {
        SqliteErrorCode = sqliteErrorCode;
    }
}
