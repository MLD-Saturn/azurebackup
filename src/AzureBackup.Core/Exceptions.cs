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
/// Exception thrown when backup/restore operations fail.
/// </summary>
public class BackupOperationException : Exception
{
    public BackupOperationType OperationType { get; }
    public string? FilePath { get; }

    public BackupOperationException(string message, BackupOperationType operationType) : base(message)
    {
        OperationType = operationType;
    }

    public BackupOperationException(string message, BackupOperationType operationType, string filePath) 
        : base(message)
    {
        OperationType = operationType;
        FilePath = filePath;
    }

    public BackupOperationException(string message, BackupOperationType operationType, Exception innerException) 
        : base(message, innerException)
    {
        OperationType = operationType;
    }
}

public enum BackupOperationType
{
    Chunking,
    Encryption,
    Upload,
    Download,
    Restore,
    MetadataSync
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
