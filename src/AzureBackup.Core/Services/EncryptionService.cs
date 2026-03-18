using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AzureBackup.Core.Services;

/// <summary>
/// Provides client-side encryption using AES-256-GCM with Argon2id key derivation.
/// All encryption happens locally before data leaves the machine, ensuring zero-knowledge.
/// </summary>
public class EncryptionService : IDisposable
{
    private byte[]? _derivedKey;
    private bool _disposed;
    private readonly Lock _keyLock = new();

    // Argon2id parameters - secure defaults for password hashing
    private const int Argon2DegreeOfParallelism = 8;
    private const int Argon2MemorySize = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int KeySize = 32; // 256 bits for AES-256
    private const int SaltSize = 16;
    private const int NonceSize = 12; // 96 bits for AES-GCM
    private const int TagSize = 16; // 128 bits for GCM authentication tag
    private const int ChecksumSize = 4; // CRC32 for corruption detection

    // Magic bytes to identify our encrypted format and detect corruption
    private static readonly byte[] MagicHeader = "AZBK"u8.ToArray();
    
    // Format version for backward compatibility
    private const byte CurrentFormatVersion = 1;

    public bool IsInitialized => _derivedKey != null;
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Encryption] {message}");
    }

    /// <summary>
    /// Generates a cryptographically secure random salt for key derivation.
    /// </summary>
    public static byte[] GenerateSalt()
    {
        byte[] salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    /// <summary>
    /// Derives an encryption key from a password using Argon2id.
    /// This is a computationally expensive operation by design.
    /// The returned key should be zeroed after use.
    /// </summary>
    public async Task<byte[]> DeriveKeyAsync(string password, byte[] salt)
    {
        Log("DeriveKeyAsync: Starting key derivation with Argon2id");
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        
        
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be {SaltSize} bytes", nameof(salt));

        // Convert password to bytes and zero after use
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using Argon2id argon2 = new(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations
            };

            var result = await argon2.GetBytesAsync(KeySize);
            Log("DeriveKeyAsync: Key derivation completed");
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }



    /// <summary>
    /// Initializes the encryption service with a derived key.
    /// </summary>
    public void Initialize(byte[] derivedKey)
    {
        Log("Initialize: Initializing encryption service");
        ArgumentNullException.ThrowIfNull(derivedKey);
        if (derivedKey.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(derivedKey));

        lock (_keyLock)
        {
            _derivedKey = new byte[KeySize];
            Array.Copy(derivedKey, _derivedKey, KeySize);
        }
    }

    /// <summary>
    /// Computes HMAC-SHA256 of the input using the derived encryption key.
    /// Returns the result as an uppercase hex string.
    /// Used for generating metadata blob names that cannot be guessed without the key,
    /// preventing an attacker with storage access from confirming file paths via dictionary attack.
    /// </summary>
    public string ComputeHmacHex(ReadOnlySpan<byte> data)
    {
        Span<byte> keyCopy = stackalloc byte[KeySize];
        lock (_keyLock)
        {
            EnsureInitialized();
            _derivedKey.AsSpan().CopyTo(keyCopy);
        }

        try
        {
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(keyCopy, data, hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Computes HMAC-SHA256 of a string using the derived encryption key.
    /// Convenience overload for path-based blob name generation.
    /// </summary>
    public string ComputeHmacHex(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return ComputeHmacHex(Encoding.UTF8.GetBytes(input));
    }

    /// <summary>
    /// Creates a verification hash that can be stored to verify the password later.
    /// This is NOT the encryption key - it's a separate derivation for verification only.
    /// </summary>
    public async Task<byte[]> CreatePasswordVerificationHashAsync(string password, byte[] salt)
    {
        // Use a different context to derive a verification hash
        byte[] verificationSalt = new byte[SaltSize];
        Array.Copy(salt, verificationSalt, SaltSize);
        verificationSalt[0] ^= 0xFF; // Modify salt to get different derivation

        // Convert password to bytes and zero after use
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using Argon2id argon2 = new(passwordBytes)
            {
                Salt = verificationSalt,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations
            };

            return await argon2.GetBytesAsync(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Verifies a password against a stored verification hash.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string password, byte[] salt, byte[] storedHash)
    {
        var computedHash = await CreatePasswordVerificationHashAsync(password, salt);
        return CryptographicOperations.FixedTimeEquals(computedHash, storedHash);
    }

    /// <summary>
    /// Encrypts data using AES-256-GCM with integrity protection.
    /// Format: [4-byte magic][1-byte version][12-byte nonce][ciphertext][16-byte tag][4-byte CRC32]
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] keyCopy;
        lock (_keyLock)
        {
            EnsureInitialized();
            keyCopy = new byte[KeySize];
            Array.Copy(_derivedKey!, keyCopy, KeySize);
        }

        try
        {
            // Single allocation for the entire output:
            // magic(4) + version(1) + nonce(12) + ciphertext(N) + tag(16) + checksum(4)
            var headerSize = MagicHeader.Length + 1 + NonceSize;
            var totalSize = headerSize + plaintext.Length + TagSize + ChecksumSize;
            byte[] result = new byte[totalSize];

            // Write header directly into result buffer
            var offset = 0;
            MagicHeader.CopyTo(result, offset);
            offset += MagicHeader.Length;

            result[offset] = CurrentFormatVersion;
            offset += 1;

            // Generate nonce directly into result buffer
            var nonceSpan = result.AsSpan(offset, NonceSize);
            RandomNumberGenerator.Fill(nonceSpan);
            offset += NonceSize;

            // Encrypt directly into result buffer (ciphertext + tag regions)
            var ciphertextSpan = result.AsSpan(offset, plaintext.Length);
            var tagSpan = result.AsSpan(offset + plaintext.Length, TagSize);

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Encrypt(nonceSpan, plaintext, ciphertextSpan, tagSpan);

            // Compute CRC32 over everything before the checksum slot
            var dataForChecksum = result.AsSpan(0, totalSize - ChecksumSize);
            WriteChecksum(dataForChecksum, result.AsSpan(totalSize - ChecksumSize));

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Decrypts data that was encrypted with AES-256-GCM.
    /// Verifies integrity before decryption.
    /// Expected format: [4-byte magic][1-byte version][12-byte nonce][ciphertext][16-byte tag][4-byte CRC32]
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> encryptedData)
    {
        // Minimum: magic(4) + version(1) + nonce(12) + tag(16) + checksum(4) = 37 bytes (empty plaintext)
        var minLength = MagicHeader.Length + 1 + NonceSize + TagSize + ChecksumSize;
        if (encryptedData.Length < minLength)
        {
            Log($"Decrypt: Data too short ({encryptedData.Length} bytes, min={minLength})");
            throw new CryptographicException("Encrypted data is too short or corrupted");
        }

        // Verify checksum first
        var dataWithoutChecksum = encryptedData[..^ChecksumSize];
        var storedChecksum = encryptedData[^ChecksumSize..];
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        WriteChecksum(dataWithoutChecksum, computedChecksum);

        if (!storedChecksum.SequenceEqual(computedChecksum))
        {
            Log($"Decrypt: CRC32 checksum mismatch (data length={encryptedData.Length})");
            throw new DataIntegrityException("Data integrity check failed - file may be corrupted");
        }

        // Verify magic header
        var magic = dataWithoutChecksum[..MagicHeader.Length];
        if (!magic.SequenceEqual(MagicHeader))
        {
            Log($"Decrypt: Invalid magic header (got {Convert.ToHexString(magic)}, expected {Convert.ToHexString(MagicHeader)})");
            throw new DataIntegrityException("Invalid data format - not encrypted by this application");
        }

        // Check version
        var version = dataWithoutChecksum[MagicHeader.Length];
        if (version > CurrentFormatVersion)
        {
            Log($"Decrypt: Unsupported format version {version} (max={CurrentFormatVersion})");
            throw new DataIntegrityException($"Unsupported encryption format version {version}. Please update the application.");
        }

        byte[] keyCopy;
        lock (_keyLock)
        {
            EnsureInitialized();
            keyCopy = new byte[KeySize];
            Array.Copy(_derivedKey!, keyCopy, KeySize);
        }

        try
        {
            var offset = MagicHeader.Length + 1; // Skip magic + version
            var nonce = dataWithoutChecksum.Slice(offset, NonceSize);
            offset += NonceSize;

            var ciphertextLength = dataWithoutChecksum.Length - MagicHeader.Length - 1 - NonceSize - TagSize;
            var ciphertext = dataWithoutChecksum.Slice(offset, ciphertextLength);
            offset += ciphertextLength;

            var tag = dataWithoutChecksum.Slice(offset, TagSize);

            byte[] plaintext = new byte[ciphertextLength];

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }
        catch (CryptographicException ex)
        {
            Log($"Decrypt: AES-GCM decryption failed (data length={encryptedData.Length}): {ex.Message}");
            throw new DataIntegrityException("Decryption failed - wrong password or corrupted data");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Attempts best-effort decryption, skipping the CRC32 integrity check.
    /// Use this for corrupted file recovery — if the CRC trailer bytes were corrupted
    /// but the ciphertext and AES-GCM tag are intact, decryption will succeed.
    /// Returns null if decryption is completely impossible (AES-GCM tag mismatch).
    /// </summary>
    public byte[]? DecryptBestEffort(ReadOnlySpan<byte> encryptedData)
    {
        var minLength = MagicHeader.Length + 1 + NonceSize + TagSize + ChecksumSize;
        if (encryptedData.Length < minLength)
        {
            Log($"DecryptBestEffort: Data too short ({encryptedData.Length} bytes, min={minLength})");
            return null;
        }

        // Strip checksum but do NOT verify it
        var dataWithoutChecksum = encryptedData[..^ChecksumSize];

        // Verify magic header (if this is wrong, data is not ours at all)
        var magic = dataWithoutChecksum[..MagicHeader.Length];
        if (!magic.SequenceEqual(MagicHeader))
        {
            Log($"DecryptBestEffort: Invalid magic header");
            return null;
        }

        var version = dataWithoutChecksum[MagicHeader.Length];
        if (version > CurrentFormatVersion)
        {
            Log($"DecryptBestEffort: Unsupported format version {version}");
            return null;
        }

        byte[] keyCopy;
        lock (_keyLock)
        {
            EnsureInitialized();
            keyCopy = new byte[KeySize];
            Array.Copy(_derivedKey!, keyCopy, KeySize);
        }

        try
        {
            var offset = MagicHeader.Length + 1;
            var nonce = dataWithoutChecksum.Slice(offset, NonceSize);
            offset += NonceSize;

            var ciphertextLength = dataWithoutChecksum.Length - MagicHeader.Length - 1 - NonceSize - TagSize;
            var ciphertext = dataWithoutChecksum.Slice(offset, ciphertextLength);
            offset += ciphertextLength;

            var tag = dataWithoutChecksum.Slice(offset, TagSize);

            byte[] plaintext = new byte[ciphertextLength];

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            Log($"DecryptBestEffort: Decrypted {ciphertextLength} bytes (CRC invalid but AES-GCM tag OK)");
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            Log($"DecryptBestEffort: AES-GCM also failed, data unrecoverable: {ex.Message}");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Encrypts a string and returns base64-encoded result.
    /// Useful for encrypting file paths and metadata.
    /// </summary>
    public string EncryptString(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = Encrypt(bytes);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a base64-encoded encrypted string.
    /// </summary>
    public string DecryptString(string encryptedBase64)
    {
        ArgumentNullException.ThrowIfNull(encryptedBase64);
        var encrypted = Convert.FromBase64String(encryptedBase64);
        var decrypted = Decrypt(encrypted);
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Writes CRC32 checksum directly into destination span, avoiding a heap allocation.
    /// </summary>
    private static void WriteChecksum(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var crc = System.IO.Hashing.Crc32.HashToUInt32(data);
        BitConverter.TryWriteBytes(destination, crc);
    }

    private void EnsureInitialized()
    {
        if (_derivedKey == null)
            throw new InvalidOperationException("Encryption service not initialized. Call Initialize() first.");
    }

    /// <summary>
    /// Securely clears the derived key from memory.
    /// After calling this, the service will need to be re-initialized.
    /// </summary>
    public void ClearKey()
    {
        lock (_keyLock)
        {
            if (_derivedKey != null)
            {
                CryptographicOperations.ZeroMemory(_derivedKey);
                _derivedKey = null;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_keyLock)
            {
                if (_derivedKey != null)
                {
                    CryptographicOperations.ZeroMemory(_derivedKey);
                    _derivedKey = null;
                }
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
