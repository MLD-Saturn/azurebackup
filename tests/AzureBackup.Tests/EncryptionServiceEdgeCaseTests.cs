using System.Security.Cryptography;
using System.Text;
using AzureBackup.Core;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Additional edge case and parameterized tests for EncryptionService.
/// </summary>
public class EncryptionServiceEdgeCaseTests : IDisposable
{
    private readonly EncryptionService _encryptionService;

    public EncryptionServiceEdgeCaseTests()
    {
        _encryptionService = new EncryptionService();
        InitializeWithTestKey();
    }

    public void Dispose()
    {
        _encryptionService.Dispose();
    }

    #region Parameterized Tests

    [Theory]
    [InlineData(1)]           // Minimum
    [InlineData(16)]          // AES block size
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(65536)]       // 64KB
    [InlineData(1048576)]     // 1MB
    public void EncryptDecrypt_VariousSizes_RoundTripSucceeds(int size)
    {
        // Arrange
        byte[] data = new byte[size];
        RandomNumberGenerator.Fill(data);

        // Act
        var encrypted = _encryptionService.Encrypt(data);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(data, decrypted);
    }

    [Theory]
    [InlineData("")]                           // Empty string
    [InlineData("Hello")]                      // ASCII
    [InlineData("?????? ???")]                 // Cyrillic
    [InlineData("????")]                    // Chinese
    [InlineData("??????")]                      // Emojis
    [InlineData("C:\\Users\\Test\\??\\file.txt")] // Mixed path
    [InlineData("Line1\r\nLine2\r\n")]         // CRLF
    [InlineData("Tab\there\tand\tthere")]      // Tabs
    [InlineData("\0\0\0")]                     // Null characters
    public void EncryptDecryptString_SpecialCharacters_RoundTripSucceeds(string input)
    {
        // Act
        var encrypted = _encryptionService.EncryptString(input);
        var decrypted = _encryptionService.DecryptString(encrypted);

        // Assert
        Assert.Equal(input, decrypted);
    }

    [Theory]
    [InlineData(15)]  // Too short
    [InlineData(16)]  // Half size
    [InlineData(31)]  // One byte short
    [InlineData(33)]  // One byte too long
    [InlineData(64)]  // Double size
    public void Initialize_WrongKeySize_ThrowsArgumentException(int keySize)
    {
        // Arrange
        using EncryptionService service = new();
        byte[] wrongKey = new byte[keySize];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => service.Initialize(wrongKey));
    }

    #endregion

    #region Binary Edge Cases

    [Fact]
    public void EncryptDecrypt_AllZeroBytes_RoundTripSucceeds()
    {
        // Arrange
        byte[] data = new byte[1000]; // All zeros

        // Act
        var encrypted = _encryptionService.Encrypt(data);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(data, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_AllOneBytes_RoundTripSucceeds()
    {
        // Arrange
        byte[] data = new byte[1000];
        Array.Fill(data, (byte)0xFF);

        // Act
        var encrypted = _encryptionService.Encrypt(data);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(data, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RepeatingPattern_RoundTripSucceeds()
    {
        // Arrange - Repeating pattern that could expose ECB-like vulnerabilities
        byte[] pattern = [0x01, 0x02, 0x03, 0x04];
        byte[] data = new byte[4000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = pattern[i % pattern.Length];
        }

        // Act
        var encrypted = _encryptionService.Encrypt(data);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(data, decrypted);
        
        // Also verify encryption doesn't have repeating patterns (would indicate ECB mode)
        var chunk1 = encrypted[17..33]; // After header
        var chunk2 = encrypted[33..49];
        Assert.NotEqual(chunk1, chunk2); // Should be different due to GCM mode
    }

    #endregion

    #region Corruption Scenarios

    [Fact]
    public void Decrypt_CorruptedMagicHeader_ThrowsDataIntegrityException()
    {
        // Arrange
        var data = "Test"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        encrypted[0] = 0x00; // Corrupt magic header

        // Act & Assert
        var ex = Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
        Assert.Contains("integrity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_CorruptedVersion_ThrowsDataIntegrityException()
    {
        // Arrange
        var data = "Test"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        encrypted[4] = 0xFF; // Set version to 255 (unsupported)

        // Act & Assert - Checksum will fail first
        Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_CorruptedNonce_ThrowsDataIntegrityException()
    {
        // Arrange
        var data = "Test"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        encrypted[5] ^= 0xFF; // Corrupt nonce

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_CorruptedTag_ThrowsDataIntegrityException()
    {
        // Arrange
        var data = "Test data for tag test"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Corrupt the GCM tag (last 4 bytes are checksum, 16 bytes before that are tag)
        encrypted[^20] ^= 0xFF;

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
    }

    [Theory]
    [InlineData(0)]   // Empty
    [InlineData(4)]   // Just magic
    [InlineData(5)]   // Magic + version
    [InlineData(17)]  // Magic + version + nonce
    [InlineData(33)]  // Missing checksum
    public void Decrypt_TruncatedAtVariousPoints_ThrowsException(int keepBytes)
    {
        // Arrange
        var data = "Test data that will be truncated"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        var truncated = encrypted[..Math.Min(keepBytes, encrypted.Length)];

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => _encryptionService.Decrypt(truncated));
    }

    #endregion

    #region Concurrent Access Tests

    [Fact]
    public async Task EncryptDecrypt_ConcurrentOperations_AllSucceed()
    {
        // Arrange
        List<Task<bool>> tasks = new();
        var testData = Enumerable.Range(0, 100)
            .Select(i => Encoding.UTF8.GetBytes($"Test message {i}"))
            .ToList();

        // Act - Concurrent encryption and decryption
        foreach (var data in testData)
        {
            tasks.Add(Task.Run(() =>
            {
                var encrypted = _encryptionService.Encrypt(data);
                var decrypted = _encryptionService.Decrypt(encrypted);
                return data.SequenceEqual(decrypted);
            }));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
    }

    [Fact]
    public async Task Encrypt_HighConcurrency_NeverProducesSameOutput()
    {
        // Arrange
        var data = "Same data for all"u8.ToArray();
        System.Collections.Concurrent.ConcurrentBag<byte[]> encryptedResults = new();

        // Act - Encrypt same data 100 times concurrently
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            encryptedResults.Add(_encryptionService.Encrypt(data));
        }));

        await Task.WhenAll(tasks);

        // Assert - All should be unique due to random nonce
        var uniqueResults = encryptedResults
            .Select(e => Convert.ToBase64String(e))
            .Distinct()
            .Count();
        
        Assert.Equal(100, uniqueResults);
    }

    #endregion

    #region Password/Salt Edge Cases

    [Theory]
    [InlineData("a")]                          // Minimum length
    [InlineData("password")]                   // Common weak password
    [InlineData("ThisIsAVeryLongPasswordThatExceeds64CharactersInLengthToTestLongInputHandling")]
    public async Task DeriveKey_VariousPasswordLengths_Succeeds(string password)
    {
        // Arrange
        using EncryptionService service = new();
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key = await service.DeriveKeyAsync(password, salt);

        // Assert
        Assert.NotNull(key);
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public async Task DeriveKey_EmptyPassword_ThrowsArgumentException()
    {
        // Arrange
        using EncryptionService service = new();
        var salt = EncryptionService.GenerateSalt();

        // Act & Assert - Argon2 requires non-empty password
        await Assert.ThrowsAsync<ArgumentException>(() => 
            service.DeriveKeyAsync("", salt));
    }

    [Fact]
    public async Task DeriveKey_WhitespacePassword_ProducesUniqueKey()
    {
        // Arrange
        using EncryptionService service = new();
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key1 = await service.DeriveKeyAsync(" ", salt);
        var key2 = await service.DeriveKeyAsync("  ", salt);

        // Assert - Different whitespace = different keys
        Assert.NotEqual(key1, key2);
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Encrypt_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        using EncryptionService service = new();
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        service.Initialize(key);
        service.Dispose();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => service.Encrypt("test"u8.ToArray()));
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        EncryptionService service = new();
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        service.Initialize(key);

        // Act & Assert - Should not throw
        service.Dispose();
        service.Dispose();
        service.Dispose();
    }

    #endregion

    private void InitializeWithTestKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        _encryptionService.Initialize(key);
    }
}
