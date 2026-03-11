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
    private readonly object _keyLock = new();

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
            byte[] nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Combine: magic + version + nonce + ciphertext + tag
            byte[] resultWithoutChecksum = new byte[MagicHeader.Length + 1 + NonceSize + ciphertext.Length + TagSize];
            var offset = 0;

            MagicHeader.CopyTo(resultWithoutChecksum, offset);
            offset += MagicHeader.Length;
            
            resultWithoutChecksum[offset] = CurrentFormatVersion;
            offset += 1;

            nonce.CopyTo(resultWithoutChecksum, offset);
            offset += NonceSize;

            ciphertext.CopyTo(resultWithoutChecksum, offset);
            offset += ciphertext.Length;

            tag.CopyTo(resultWithoutChecksum, offset);

            // Add CRC32 checksum for corruption detection
            var checksum = ComputeChecksum(resultWithoutChecksum);
            byte[] result = new byte[resultWithoutChecksum.Length + ChecksumSize];
            resultWithoutChecksum.CopyTo(result, 0);
            checksum.CopyTo(result, resultWithoutChecksum.Length);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Encrypts data from a stream and writes to output stream.
    /// Processes in chunks to handle large files efficiently.
    /// Uses atomic writes to prevent partial corruption.
    /// </summary>
    public async Task EncryptStreamAsync(Stream input, Stream output, int bufferSize = 1024 * 1024)
    {
        EnsureInitialized();

        byte[] buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = await input.ReadAsync(buffer)) > 0)
        {
            byte[] chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);
            var encrypted = Encrypt(chunkData);

            // Write chunk atomically: combine length + data into single write
            byte[] chunkPacket = new byte[4 + encrypted.Length];
            BitConverter.GetBytes(encrypted.Length).CopyTo(chunkPacket, 0);
            encrypted.CopyTo(chunkPacket, 4);
            
            await output.WriteAsync(chunkPacket);
        }

        // Write end marker
        await output.WriteAsync(BitConverter.GetBytes(0));
        await output.FlushAsync();
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
            throw new CryptographicException("Encrypted data is too short or corrupted");

        // Verify checksum first
        var dataWithoutChecksum = encryptedData[..^ChecksumSize];
        var storedChecksum = encryptedData[^ChecksumSize..];
        var computedChecksum = ComputeChecksum(dataWithoutChecksum);
        
        if (!storedChecksum.SequenceEqual(computedChecksum))
            throw new DataIntegrityException("Data integrity check failed - file may be corrupted");

        // Verify magic header
        var magic = dataWithoutChecksum[..MagicHeader.Length];
        if (!magic.SequenceEqual(MagicHeader))
            throw new DataIntegrityException("Invalid data format - not encrypted by this application");

        // Check version
        var version = dataWithoutChecksum[MagicHeader.Length];
        if (version > CurrentFormatVersion)
            throw new DataIntegrityException($"Unsupported encryption format version {version}. Please update the application.");

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
        catch (CryptographicException)
        {
            throw new DataIntegrityException("Decryption failed - wrong password or corrupted data");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Decrypts data from a stream and writes to output stream.
    /// </summary>
    public async Task DecryptStreamAsync(Stream input, Stream output)
    {
        EnsureInitialized();

        byte[] lengthBuffer = new byte[4];

        while (true)
        {
            var bytesRead = await input.ReadAsync(lengthBuffer);
            if (bytesRead < 4) 
                throw new DataIntegrityException("Unexpected end of stream - file may be truncated");

            var chunkLength = BitConverter.ToInt32(lengthBuffer);
            if (chunkLength == 0) break; // End marker
            
            if (chunkLength < 0 || chunkLength > 100_000_000) // Sanity check: max 100MB chunk
                throw new DataIntegrityException("Invalid chunk length - file may be corrupted");

            byte[] encryptedChunk = new byte[chunkLength];
            await input.ReadExactlyAsync(encryptedChunk);

            var decrypted = Decrypt(encryptedChunk);
            await output.WriteAsync(decrypted);
        }
        
        await output.FlushAsync();
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
    /// Computes CRC32 checksum for corruption detection.
    /// </summary>
    private static byte[] ComputeChecksum(ReadOnlySpan<byte> data)
    {
        var crc = System.IO.Hashing.Crc32.HashToUInt32(data);
        return BitConverter.GetBytes(crc);
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
