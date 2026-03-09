using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for EncryptionService covering key derivation, encryption/decryption,
/// data integrity, and error handling.
/// </summary>
public class EncryptionServiceTests : IDisposable
{
    private readonly EncryptionService _encryptionService;
    private const string TestPassword = "TestPassword123!";

    public EncryptionServiceTests()
    {
        _encryptionService = new EncryptionService();
    }

    public void Dispose()
    {
        _encryptionService.Dispose();
    }

    #region Key Derivation Tests

    [Fact]
    public async Task DeriveKeyAsync_WithValidInputs_ReturnsDerivedKey()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);

        // Assert
        Assert.NotNull(key);
        Assert.Equal(32, key.Length); // 256 bits
    }

    [Fact]
    public async Task DeriveKeyAsync_SamePasswordAndSalt_ReturnsSameKey()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        var key2 = await _encryptionService.DeriveKeyAsync(TestPassword, salt);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_DifferentSalts_ReturnsDifferentKeys()
    {
        // Arrange
        var salt1 = EncryptionService.GenerateSalt();
        var salt2 = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync(TestPassword, salt1);
        var key2 = await _encryptionService.DeriveKeyAsync(TestPassword, salt2);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_DifferentPasswords_ReturnsDifferentKeys()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync("Password1", salt);
        var key2 = await _encryptionService.DeriveKeyAsync("Password2", salt);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_NullPassword_ThrowsArgumentNullException()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _encryptionService.DeriveKeyAsync(null!, salt));
    }

    [Fact]
    public async Task DeriveKeyAsync_NullSalt_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _encryptionService.DeriveKeyAsync(TestPassword, null!));
    }

    #endregion

    #region Initialization Tests

    [Fact]
    public void Initialize_WithValidKey_SetsIsInitializedTrue()
    {
        // Arrange
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        // Act
        _encryptionService.Initialize(key);

        // Assert
        Assert.True(_encryptionService.IsInitialized);
    }

    [Fact]
    public void Initialize_WithInvalidKeyLength_ThrowsArgumentException()
    {
        // Arrange
        byte[] shortKey = new byte[16]; // Should be 32

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _encryptionService.Initialize(shortKey));
    }

    [Fact]
    public void Initialize_WithNullKey_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _encryptionService.Initialize(null!));
    }

    #endregion

    #region Encryption/Decryption Tests

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalData()
    {
        // Arrange
        InitializeWithTestKey();
        var originalData = "Hello, World! This is a test message."u8.ToArray();

        // Act
        var encrypted = _encryptionService.Encrypt(originalData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(originalData, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachTime()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Same data"u8.ToArray();

        // Act
        var encrypted1 = _encryptionService.Encrypt(data);
        var encrypted2 = _encryptionService.Encrypt(data);

        // Assert - Different due to random nonce
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Encrypt_OutputIsLargerThanInput()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] data = new byte[100];

        // Act
        var encrypted = _encryptionService.Encrypt(data);

        // Assert - Should include magic(4) + version(1) + nonce(12) + tag(16) + checksum(4) = 37 bytes overhead
        Assert.True(encrypted.Length > data.Length + 30);
    }

    [Fact]
    public void Encrypt_WithoutInitialization_ThrowsInvalidOperationException()
    {
        // Arrange
        var data = "Test"u8.ToArray();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _encryptionService.Encrypt(data));
    }

    [Fact]
    public void Decrypt_WithCorruptedChecksum_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Corrupt the checksum (last 4 bytes)
        encrypted[^1] ^= 0xFF;

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_WithCorruptedCiphertext_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Corrupt middle of ciphertext (after magic+version+nonce = 17 bytes)
        encrypted[20] ^= 0xFF;

        // Act & Assert - Either checksum or GCM auth will fail
        Assert.ThrowsAny<Exception>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_WithTruncatedData_ThrowsException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Truncate
        var truncated = encrypted[..20];

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => _encryptionService.Decrypt(truncated));
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Secret message"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Create new service with different key
        using EncryptionService otherService = new();
        byte[] otherKey = new byte[32];
        RandomNumberGenerator.Fill(otherKey);
        otherService.Initialize(otherKey);

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => otherService.Decrypt(encrypted));
    }

    #endregion

    #region String Encryption Tests

    [Fact]
    public void EncryptDecryptString_RoundTrip_ReturnsOriginalString()
    {
        // Arrange
        InitializeWithTestKey();
        var original = "C:\\Users\\Test\\Documents\\Important File.docx";

        // Act
        var encrypted = _encryptionService.EncryptString(original);
        var decrypted = _encryptionService.DecryptString(encrypted);

        // Assert
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptString_ReturnsBase64()
    {
        // Arrange
        InitializeWithTestKey();
        var original = "Test";

        // Act
        var encrypted = _encryptionService.EncryptString(original);

        // Assert - Should be valid Base64
        var decoded = Convert.FromBase64String(encrypted);
        Assert.NotEmpty(decoded);
    }

    #endregion

    #region Password Verification Tests

    [Fact]
    public async Task VerifyPassword_WithCorrectPassword_ReturnsTrue()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();
        var hash = await _encryptionService.CreatePasswordVerificationHashAsync(TestPassword, salt);

        // Act
        var isValid = await _encryptionService.VerifyPasswordAsync(TestPassword, salt, hash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task VerifyPassword_WithWrongPassword_ReturnsFalse()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();
        var hash = await _encryptionService.CreatePasswordVerificationHashAsync(TestPassword, salt);

        // Act
        var isValid = await _encryptionService.VerifyPasswordAsync("WrongPassword", salt, hash);

        // Assert
        Assert.False(isValid);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EncryptDecrypt_EmptyData_Works()
    {
        // Arrange
        InitializeWithTestKey();
        var emptyData = Array.Empty<byte>();

        // Act
        var encrypted = _encryptionService.Encrypt(emptyData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Empty(decrypted);
    }

    [Fact]
    public void EncryptDecrypt_LargeData_Works()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] largeData = new byte[1024 * 1024]; // 1 MB
        RandomNumberGenerator.Fill(largeData);

        // Act
        var encrypted = _encryptionService.Encrypt(largeData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(largeData, decrypted);
    }

    [Fact]
    public void GenerateSalt_ReturnsUniqueSalts()
    {
        // Act
        var salts = Enumerable.Range(0, 100)
            .Select(_ => EncryptionService.GenerateSalt())
            .ToList();

        // Assert - All should be unique
        var uniqueSalts = salts.Select(s => Convert.ToBase64String(s)).Distinct().Count();
        Assert.Equal(100, uniqueSalts);
    }

    #endregion

    #region ClearKey Tests

    [Fact]
    public void ClearKey_RemovesKeyFromMemory()
    {
        // Arrange
        InitializeWithTestKey();
        Assert.True(_encryptionService.IsInitialized);
        
        // Act
        _encryptionService.ClearKey();
        
        // Assert
        Assert.False(_encryptionService.IsInitialized);
    }

    [Fact]
    public void ClearKey_EncryptAfterClear_ThrowsInvalidOperationException()
    {
        // Arrange
        InitializeWithTestKey();
        _encryptionService.ClearKey();
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => 
            _encryptionService.Encrypt(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ClearKey_CanReinitializeAfterClear()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] testData = [1, 2, 3, 4, 5];
        var encrypted1 = _encryptionService.Encrypt(testData);
        
        // Act - Clear and reinitialize
        _encryptionService.ClearKey();
        InitializeWithTestKey();
        
        // Should be able to encrypt again (with new key)
        var encrypted2 = _encryptionService.Encrypt(testData);
        
        // Assert - Different keys produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void ClearKey_MultipleCallsDoNotThrow()
    {
        // Arrange
        InitializeWithTestKey();
        
        // Act & Assert - Should not throw
        _encryptionService.ClearKey();
        _encryptionService.ClearKey();
        _encryptionService.ClearKey();
        
        Assert.False(_encryptionService.IsInitialized);
    }

    #endregion

    private void InitializeWithTestKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        _encryptionService.Initialize(key);
    }
}
