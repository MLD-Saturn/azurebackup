using System.Security.Cryptography;
using LiteDB;

namespace AzureBackup.Core.Services;

/// <summary>
/// Static methods for database existence checks and format migration.
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Checks if a database file exists at the given path.
    /// Used to determine if this is a new user or returning user before password entry.
    /// </summary>
    /// <param name="databasePath">Path to check for database file</param>
    /// <returns>True if database file exists</returns>
    public static bool DatabaseExists(string databasePath)
    {
        return File.Exists(databasePath);
    }

    /// <summary>
    /// Checks if a database has an associated Argon2id salt file.
    /// Databases without a salt file are using the legacy encryption method.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database has a salt file (using new Argon2id encryption)</returns>
    public static bool HasArgon2idSalt(string databasePath)
    {
        var saltPath = GetSaltFilePath(databasePath);
        return File.Exists(saltPath);
    }

    /// <summary>
    /// Checks if an existing database uses the legacy encryption method (raw password without Argon2id).
    /// Legacy databases exist but have no .salt file.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database exists and uses legacy encryption</returns>
    public static bool IsLegacyEncryptedDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
            return false;
        
        // If it's unencrypted, it's not legacy encrypted
        if (IsUnencryptedDatabase(databasePath))
            return false;
        
        // If it has a salt file, it's using new Argon2id encryption
        if (HasArgon2idSalt(databasePath))
            return false;
        
        // Database exists, is encrypted, but has no salt file = legacy encryption
        return true;
    }

    /// <summary>
    /// Checks if an existing database is unencrypted (legacy format).
    /// Used to detect if migration is needed.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database exists and is unencrypted</returns>
    public static bool IsUnencryptedDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
            return false;

        try
        {
            // Try to open without password - if it works, it's unencrypted
            using var db = new LiteDatabase(databasePath);
            // Try to read something to verify it's a valid database
            var _ = db.GetCollectionNames().ToList();
            return true;
        }
        catch
        {
            // Either not a database or is encrypted
            return false;
        }
    }

    /// <summary>
    /// Migrates an unencrypted database to an encrypted one.
    /// Creates a new encrypted database with Argon2id key derivation and copies all data.
    /// </summary>
    /// <param name="sourcePath">Path to the unencrypted database</param>
    /// <param name="targetPath">Path for the new encrypted database</param>
    /// <param name="password">Password to encrypt the new database</param>
    public static void MigrateToEncrypted(string sourcePath, string targetPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source database not found", sourcePath);

        // Open source (unencrypted)
        using var sourceDb = new LiteDatabase(sourcePath);
        CopyToEncryptedDatabase(sourceDb, targetPath, password);
    }

    /// <summary>
    /// Migrates a legacy encrypted database (raw password, no Argon2id) to the new format.
    /// Creates a new database with Argon2id key derivation and copies all data.
    /// </summary>
    /// <param name="sourcePath">Path to the legacy encrypted database</param>
    /// <param name="targetPath">Path for the new encrypted database</param>
    /// <param name="password">Password (same password used for the legacy database)</param>
    /// <exception cref="InvalidPasswordException">Thrown if password is incorrect for the legacy database</exception>
    public static void MigrateLegacyEncrypted(string sourcePath, string targetPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source database not found", sourcePath);

        // Open source with legacy encryption (raw password)
        var sourceConnString = new ConnectionString
        {
            Filename = sourcePath,
            Password = password, // Raw password - legacy method
            Connection = ConnectionType.Shared
        };
        
        LiteDatabase sourceDb;
        try
        {
            sourceDb = new LiteDatabase(sourceConnString);
            // Verify we can actually read from it
            _ = sourceDb.GetCollectionNames().ToList();
        }
        catch (LiteException ex) when (ex.Message.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
                                        ex.Message.Contains("file is not a valid", StringComparison.OrdinalIgnoreCase) ||
                                        ex.Message.Contains("HMAC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidPasswordException("Invalid password for legacy database. Please try again.", ex);
        }
        
        using (sourceDb)
        {
            CopyToEncryptedDatabase(sourceDb, targetPath, password);
        }
    }

    /// <summary>
    /// Creates a new Argon2id-encrypted database and copies all collections from the source.
    /// Shared by <see cref="MigrateToEncrypted"/> and <see cref="MigrateLegacyEncrypted"/>.
    /// </summary>
    private static void CopyToEncryptedDatabase(LiteDatabase sourceDb, string targetPath, string password)
    {
        // Generate salt for the new encrypted database
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        
        // Save the salt file
        var saltFilePath = GetSaltFilePath(targetPath);
        File.WriteAllBytes(saltFilePath, salt);
        
        // Derive strong key using Argon2id
        var derivedKey = DeriveKeyFromPassword(password, salt);
        
        try
        {
            var dbPassword = Convert.ToBase64String(derivedKey);
            
            // Create target (encrypted with derived key)
            var targetConnString = new ConnectionString
            {
                Filename = targetPath,
                Password = dbPassword,
                Connection = ConnectionType.Shared
            };
            using var targetDb = new LiteDatabase(targetConnString);

            // Copy all collections
            foreach (var collectionName in sourceDb.GetCollectionNames())
            {
                var sourceCollection = sourceDb.GetCollection(collectionName);
                var targetCollection = targetDb.GetCollection(collectionName);
                
                foreach (var doc in sourceCollection.FindAll())
                {
                    targetCollection.Insert(doc);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }
}
