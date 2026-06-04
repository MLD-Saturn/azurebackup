using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using static AzureBackup.Core.KdfParameters;
using static AzureBackup.Core.Services.KdfMemoryDiagnostics;

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

    private const int KeySize = 32; // 256 bits for AES-256
    private const int NonceSize = 12; // 96 bits for AES-GCM
    private const int TagSize = 16; // 128 bits for GCM authentication tag
    private const int ChecksumSize = 4; // CRC32 for corruption detection

    // Magic bytes to identify our encrypted format and detect corruption
    private static readonly byte[] MagicHeader = "AZBK"u8.ToArray();
    
    // Format version for backward compatibility
    private const byte CurrentFormatVersion = 1;

    /// <summary>
    /// Total byte overhead added by encryption: magic(4) + version(1) + nonce(12) + tag(16) + CRC32(4) = 37.
    /// Use this to compute required buffer sizes for <see cref="EncryptInto"/> and <see cref="DecryptInto"/>:
    /// encrypted size = plaintext size + EncryptionOverhead,
    /// plaintext size = encrypted size - EncryptionOverhead.
    /// </summary>
    public const int EncryptionOverhead = 4 + 1 + NonceSize + TagSize + ChecksumSize; // 37

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
    /// <remarks>
    /// Accepts <see cref="ReadOnlyMemory{T}"/> rather than <c>string</c> so callers that
    /// hold password material in a <c>char[]</c> can keep the plaintext out of the
    /// string intern table / long-lived managed heap. The string overload is retained
    /// for compatibility but immediately copies into a span before calling this core.
    /// </remarks>
    public async Task<byte[]> DeriveKeyAsync(ReadOnlyMemory<char> password, byte[] salt)
    {
        Log("DeriveKeyAsync: Starting key derivation with Argon2id");
        ArgumentNullException.ThrowIfNull(salt);

        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be {SaltSize} bytes", nameof(salt));

        // Convert password to bytes and zero after use
        var passwordBytes = PasswordBytes.FromChars(password.Span);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation";
        var workingSetMb = ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
        Log($"DeriveKeyAsync: starting Argon2id (memory={Argon2MemorySize / 1024} MB, lanes={Argon2DegreeOfParallelism}, " +
            $"iterations={Argon2Iterations}, gcMode={gcMode}, workingSet={workingSetMb} MB)");
        try
        {
            // B11/B12: see SqliteBackend.DeriveKeyFromPassword for the
            // full rationale. Argon2id makes 8 MB-per-lane LOH allocations
            // that occasionally fail under heavy LOH fragmentation. We try
            // the secure default once, force LOH compaction on OOM, and
            // try ONCE more. Reducing MemorySize would change the derived
            // key bytes, so we never silently weaken the parameters --
            // any genuine inability to allocate surfaces as a typed
            // InsufficientMemoryForKdfException with diagnostic context.
            Exception? lastOom = null;
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
                Log($"DeriveKeyAsync: Key derivation completed in {sw.ElapsedMilliseconds} ms");
                return result;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                Log($"DeriveKeyAsync: OutOfMemoryException at {sw.ElapsedMilliseconds} ms -- {ex.Message}");
                Log($"  Pre-compaction state: workingSet={ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64)} MB, " +
                    $"GC managed={ToMegabytes(GC.GetTotalMemory(false))} MB, " +
                    $"GC available={ToMegabytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} MB");
                ForceLargeObjectHeapCompaction();
                Log($"  Post-compaction state: workingSet={ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64)} MB, " +
                    $"GC managed={ToMegabytes(GC.GetTotalMemory(false))} MB, " +
                    $"GC available={ToMegabytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} MB");
            }

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
                Log($"DeriveKeyAsync: Key derivation completed (after LOH compaction) in {sw.ElapsedMilliseconds} ms");
                return result;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                Log($"DeriveKeyAsync: OutOfMemoryException AFTER LOH compaction at {sw.ElapsedMilliseconds} ms -- giving up");
            }

            throw new InsufficientMemoryForKdfException(
                $"Unable to derive the encryption key: Argon2id key derivation could not allocate " +
                $"its {Argon2MemorySize / 1024} MB working memory after a forced LOH compaction. " +
                $"Close other applications, close VS Diagnostic Tools, or restart the machine.",
                lastOom);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Legacy <c>string</c> overload. Prefer the <see cref="ReadOnlyMemory{T}"/> overload
    /// so the plaintext password does not linger in the string intern table.
    /// </summary>
    public Task<byte[]> DeriveKeyAsync(string password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        return DeriveKeyAsync(password.AsMemory(), salt);
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
    /// Uses stackalloc for typical file paths to avoid a heap allocation per call.
    /// For longer inputs, uses a rented, zero-on-return pool buffer so the UTF-8
    /// encoded plaintext is not left on the managed heap for the GC to reclaim.
    /// </summary>
    public string ComputeHmacHex(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(input.Length);
        if (maxByteCount <= 1024)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var bytesWritten = Encoding.UTF8.GetBytes(input, buffer);
            return ComputeHmacHex(buffer[..bytesWritten]);
        }

        // Long input: rent a buffer, zero it on return so the plaintext does not
        // linger in the pooled arrays for a future consumer to peek at.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(input, rented);
            return ComputeHmacHex(rented.AsSpan(0, bytesWritten));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Creates a verification hash that can be stored to verify the password later.
    /// This is NOT the encryption key - it's a separate derivation for verification only.
    /// </summary>
    public async Task<byte[]> CreatePasswordVerificationHashAsync(ReadOnlyMemory<char> password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        // Use a different context to derive a verification hash
        byte[] verificationSalt = new byte[SaltSize];
        Array.Copy(salt, verificationSalt, SaltSize);
        verificationSalt[0] ^= 0xFF; // Modify salt to get different derivation

        // Convert password to bytes and zero after use
        var passwordBytes = PasswordBytes.FromChars(password.Span);
        // B15: instrument the verification-hash KDF identically to
        // DeriveKeyAsync. Pre-B15 this call site was silent, so an OOM
        // here surfaced only as the generic raw exception with no
        // log breadcrumbs.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation";
        var workingSetMb = ToMegabytes(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
        Log($"CreatePasswordVerificationHashAsync: starting Argon2id (memory={Argon2MemorySize / 1024} MB, " +
            $"lanes={Argon2DegreeOfParallelism}, iterations={Argon2Iterations}, gcMode={gcMode}, " +
            $"workingSet={workingSetMb} MB)");
        try
        {
            // B11/B12: see DeriveKeyAsync above for full rationale.
            Exception? lastOom = null;
            try
            {
                using Argon2id argon2 = new(passwordBytes)
                {
                    Salt = verificationSalt,
                    DegreeOfParallelism = Argon2DegreeOfParallelism,
                    MemorySize = Argon2MemorySize,
                    Iterations = Argon2Iterations
                };
                var result = await argon2.GetBytesAsync(32);
                Log($"CreatePasswordVerificationHashAsync: completed in {sw.ElapsedMilliseconds} ms");
                return result;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                Log($"CreatePasswordVerificationHashAsync: OutOfMemoryException at {sw.ElapsedMilliseconds} ms -- {ex.Message}");
                ForceLargeObjectHeapCompaction();
            }

            try
            {
                using Argon2id argon2 = new(passwordBytes)
                {
                    Salt = verificationSalt,
                    DegreeOfParallelism = Argon2DegreeOfParallelism,
                    MemorySize = Argon2MemorySize,
                    Iterations = Argon2Iterations
                };
                var result = await argon2.GetBytesAsync(32);
                Log($"CreatePasswordVerificationHashAsync: completed (after LOH compaction) in {sw.ElapsedMilliseconds} ms");
                return result;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                Log($"CreatePasswordVerificationHashAsync: OutOfMemoryException AFTER LOH compaction at {sw.ElapsedMilliseconds} ms -- giving up");
            }

            throw new InsufficientMemoryForKdfException(
                $"Unable to derive the verification hash: Argon2id could not allocate " +
                $"its {Argon2MemorySize / 1024} MB working memory after a forced LOH compaction.",
                lastOom);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="CreatePasswordVerificationHashAsync(ReadOnlyMemory{char}, byte[])"/>.
    /// </summary>
    public Task<byte[]> CreatePasswordVerificationHashAsync(string password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        return CreatePasswordVerificationHashAsync(password.AsMemory(), salt);
    }

    /// <summary>
    /// Verifies a password against a stored verification hash.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(ReadOnlyMemory<char> password, byte[] salt, byte[] storedHash)
    {
        var computedHash = await CreatePasswordVerificationHashAsync(password, salt);
        return CryptographicOperations.FixedTimeEquals(computedHash, storedHash);
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="VerifyPasswordAsync(ReadOnlyMemory{char}, byte[], byte[])"/>.
    /// </summary>
    public Task<bool> VerifyPasswordAsync(string password, byte[] salt, byte[] storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        return VerifyPasswordAsync(password.AsMemory(), salt, storedHash);
    }

    /// <summary>
    /// Encrypts data using AES-256-GCM with integrity protection.
    /// Format: [4-byte magic][1-byte version][12-byte nonce][ciphertext][16-byte tag][4-byte CRC32]
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> keyCopy = stackalloc byte[KeySize];
        CopyKey(keyCopy);

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
    /// Encrypts data into a caller-provided buffer, avoiding a heap allocation.
    /// The destination must be at least <c>plaintext.Length + <see cref="EncryptionOverhead"/></c> bytes.
    /// Use this with <see cref="System.Buffers.ArrayPool{T}"/> to eliminate LOH allocations
    /// on the upload hot path.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public int EncryptInto(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        var totalSize = plaintext.Length + EncryptionOverhead;
        if (destination.Length < totalSize)
            throw new ArgumentException(
                $"Destination too small: need {totalSize} bytes, got {destination.Length}",
                nameof(destination));

        Span<byte> keyCopy = stackalloc byte[KeySize];
        CopyKey(keyCopy);

        try
        {
            var offset = 0;
            MagicHeader.CopyTo(destination[offset..]);
            offset += MagicHeader.Length;

            destination[offset] = CurrentFormatVersion;
            offset += 1;

            var nonceSpan = destination.Slice(offset, NonceSize);
            RandomNumberGenerator.Fill(nonceSpan);
            offset += NonceSize;

            var ciphertextSpan = destination.Slice(offset, plaintext.Length);
            var tagSpan = destination.Slice(offset + plaintext.Length, TagSize);

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Encrypt(nonceSpan, plaintext, ciphertextSpan, tagSpan);

            var dataForChecksum = destination[..(totalSize - ChecksumSize)];
            WriteChecksum(dataForChecksum, destination.Slice(totalSize - ChecksumSize, ChecksumSize));

            return totalSize;
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
        var env = ValidateAndParseEnvelope(encryptedData, "Decrypt");

        Span<byte> keyCopy = stackalloc byte[KeySize];
        CopyKey(keyCopy);

        try
        {
            byte[] plaintext = new byte[env.CiphertextLength];

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Decrypt(env.Nonce, env.Ciphertext, env.Tag, plaintext);

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
    /// Decrypts data into a caller-provided buffer, avoiding a heap allocation.
    /// The destination must be at least <c>encryptedData.Length - <see cref="EncryptionOverhead"/></c> bytes.
    /// Use this with <see cref="System.Buffers.ArrayPool{T}"/> to eliminate LOH allocations
    /// on the download hot path.
    /// </summary>
    /// <returns>The number of plaintext bytes written to <paramref name="destination"/>.</returns>
    public int DecryptInto(ReadOnlySpan<byte> encryptedData, Span<byte> destination)
    {
        var env = ValidateAndParseEnvelope(encryptedData, "DecryptInto");

        if (destination.Length < env.CiphertextLength)
            throw new ArgumentException(
                $"Destination too small: need {env.CiphertextLength} bytes, got {destination.Length}",
                nameof(destination));

        Span<byte> keyCopy = stackalloc byte[KeySize];
        CopyKey(keyCopy);

        try
        {
            using AesGcm aes = new(keyCopy, TagSize);
            aes.Decrypt(env.Nonce, env.Ciphertext, env.Tag, destination[..env.CiphertextLength]);

            return env.CiphertextLength;
        }
        catch (CryptographicException ex)
        {
            Log($"DecryptInto: AES-GCM decryption failed: {ex.Message}");
            throw new DataIntegrityException("Decryption failed - wrong password or corrupted data");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Attempts best-effort decryption
    /// Use this for corrupted file recovery — if the CRC trailer bytes were corrupted
    /// but the ciphertext and AES-GCM tag are intact, decryption will succeed.
    /// Returns null if decryption is completely impossible (AES-GCM tag mismatch).
    /// </summary>
    public byte[]? DecryptBestEffort(ReadOnlySpan<byte> encryptedData)
    {
        if (!TryParseEnvelopeUnchecked(encryptedData, "DecryptBestEffort", out var env))
            return null;

        Span<byte> keyCopy = stackalloc byte[KeySize];
        CopyKey(keyCopy);

        try
        {
            byte[] plaintext = new byte[env.CiphertextLength];

            using AesGcm aes = new(keyCopy, TagSize);
            aes.Decrypt(env.Nonce, env.Ciphertext, env.Tag, plaintext);

            Log($"DecryptBestEffort: Decrypted {env.CiphertextLength} bytes (CRC invalid but AES-GCM tag OK)");
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
    /// Validates the CRC32 envelope of encrypted data without decrypting.
    /// Returns true if the CRC matches, false otherwise.
    /// Used for diagnostic verification immediately after encryption.
    /// </summary>
    public bool ValidateCrc(ReadOnlySpan<byte> encryptedData)
    {
        var minLength = MagicHeader.Length + 1 + NonceSize + TagSize + ChecksumSize;
        if (encryptedData.Length < minLength)
            return false;

        var dataWithoutChecksum = encryptedData[..^ChecksumSize];
        var storedChecksum = encryptedData[^ChecksumSize..];
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        WriteChecksum(dataWithoutChecksum, computedChecksum);

        return storedChecksum.SequenceEqual(computedChecksum);
    }

    /// <summary>
    /// Returns diagnostic info about a CRC mismatch for logging.
    /// Includes stored vs computed CRC, data length, and first/last bytes.
    /// </summary>
    public string DiagnoseCrcMismatch(ReadOnlySpan<byte> encryptedData)
    {
        var minLength = MagicHeader.Length + 1 + NonceSize + TagSize + ChecksumSize;
        if (encryptedData.Length < minLength)
            return $"Data too short: {encryptedData.Length} bytes (min={minLength})";

        var dataWithoutChecksum = encryptedData[..^ChecksumSize];
        var storedChecksum = encryptedData[^ChecksumSize..];
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        WriteChecksum(dataWithoutChecksum, computedChecksum);

        var match = storedChecksum.SequenceEqual(computedChecksum);
        var lastDataBytes = encryptedData.Length >= 8
            ? Convert.ToHexString(encryptedData[^8..])
            : Convert.ToHexString(encryptedData);

        return $"CRC {(match ? "OK" : "MISMATCH")}: " +
               $"stored={Convert.ToHexString(storedChecksum)}, " +
               $"computed={Convert.ToHexString(computedChecksum)}, " +
               $"dataLen={encryptedData.Length}, " +
               $"last8bytes={lastDataBytes}";
    }

    /// <summary>
    /// Parsed fields from an encrypted envelope. Zero-allocation ref struct
    /// holding <see cref="ReadOnlySpan{T}"/> slices into the original buffer.
    /// </summary>
    private ref struct DecryptEnvelope
    {
        public ReadOnlySpan<byte> DataWithoutChecksum;
        public ReadOnlySpan<byte> Nonce;
        public ReadOnlySpan<byte> Ciphertext;
        public ReadOnlySpan<byte> Tag;
        public int CiphertextLength;
    }

    /// <summary>
    /// Validates the encrypted envelope (CRC, magic header, version) and parses the
    /// nonce/ciphertext/tag fields. Shared by <see cref="Decrypt"/> and <see cref="DecryptInto"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Data too short.</exception>
    /// <exception cref="DataIntegrityException">CRC, magic, or version check failed.</exception>
    private DecryptEnvelope ValidateAndParseEnvelope(ReadOnlySpan<byte> encryptedData, string caller)
    {
        var minLength = EncryptionOverhead; // magic + version + nonce + tag + checksum
        if (encryptedData.Length < minLength)
        {
            Log($"{caller}: Data too short ({encryptedData.Length} bytes, min={minLength})");
            throw new CryptographicException("Encrypted data is too short or corrupted");
        }

        var dataWithoutChecksum = encryptedData[..^ChecksumSize];
        var storedChecksum = encryptedData[^ChecksumSize..];
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        WriteChecksum(dataWithoutChecksum, computedChecksum);

        if (!storedChecksum.SequenceEqual(computedChecksum))
        {
            Log($"{caller}: CRC32 checksum mismatch (data length={encryptedData.Length})");
            throw new DataIntegrityException("Data integrity check failed - file may be corrupted");
        }

        return ParseEnvelopeFields(dataWithoutChecksum, caller);
    }

    /// <summary>
    /// Parses envelope fields without CRC verification.
    /// Used by <see cref="DecryptBestEffort"/> which intentionally skips CRC.
    /// </summary>
    /// <returns>True if the envelope is valid and <paramref name="envelope"/> is populated; false otherwise.</returns>
    private bool TryParseEnvelopeUnchecked(ReadOnlySpan<byte> encryptedData, string caller, out DecryptEnvelope envelope)
    {
        envelope = default;
        var minLength = EncryptionOverhead;
        if (encryptedData.Length < minLength)
        {
            Log($"{caller}: Data too short ({encryptedData.Length} bytes, min={minLength})");
            return false;
        }

        var dataWithoutChecksum = encryptedData[..^ChecksumSize];

        var magic = dataWithoutChecksum[..MagicHeader.Length];
        if (!magic.SequenceEqual(MagicHeader))
        {
            Log($"{caller}: Invalid magic header");
            return false;
        }

        var version = dataWithoutChecksum[MagicHeader.Length];
        if (version > CurrentFormatVersion)
        {
            Log($"{caller}: Unsupported format version {version}");
            return false;
        }

        envelope = ExtractFields(dataWithoutChecksum);
        return true;
    }

    /// <summary>
    /// Validates magic header and version, then extracts nonce/ciphertext/tag fields.
    /// Throws on failure (used by strict decrypt paths).
    /// </summary>
    private DecryptEnvelope ParseEnvelopeFields(ReadOnlySpan<byte> dataWithoutChecksum, string caller)
    {
        var magic = dataWithoutChecksum[..MagicHeader.Length];
        if (!magic.SequenceEqual(MagicHeader))
        {
            Log($"{caller}: Invalid magic header (got {Convert.ToHexString(magic)}, expected {Convert.ToHexString(MagicHeader)})");
            throw new DataIntegrityException("Invalid data format - not encrypted by this application");
        }

        var version = dataWithoutChecksum[MagicHeader.Length];
        if (version > CurrentFormatVersion)
        {
            Log($"{caller}: Unsupported format version {version} (max={CurrentFormatVersion})");
            throw new DataIntegrityException($"Unsupported encryption format version {version}. Please update the application.");
        }

        return ExtractFields(dataWithoutChecksum);
    }

    /// <summary>
    /// Extracts nonce, ciphertext, and tag spans from a validated envelope.
    /// </summary>
    private static DecryptEnvelope ExtractFields(ReadOnlySpan<byte> dataWithoutChecksum)
    {
        var offset = MagicHeader.Length + 1; // Skip magic + version
        var nonce = dataWithoutChecksum.Slice(offset, NonceSize);
        offset += NonceSize;

        var ciphertextLength = dataWithoutChecksum.Length - MagicHeader.Length - 1 - NonceSize - TagSize;
        var ciphertext = dataWithoutChecksum.Slice(offset, ciphertextLength);
        offset += ciphertextLength;

        var tag = dataWithoutChecksum.Slice(offset, TagSize);

        return new DecryptEnvelope
        {
            DataWithoutChecksum = dataWithoutChecksum,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            CiphertextLength = ciphertextLength
        };
    }

    /// <summary>
    /// Copies the derived key into a caller-provided stack buffer under lock.
    /// Shared by all encrypt/decrypt methods to avoid repeating the lock pattern.
    /// </summary>
    private void CopyKey(Span<byte> destination)
    {
        lock (_keyLock)
        {
            EnsureInitialized();
            _derivedKey.AsSpan().CopyTo(destination);
        }
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
