using AzureBackup.Core;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for password strength validation and related security features.
/// </summary>
public class PasswordValidationTests : IAsyncLifetime
{
    private const string TestDbPassword = "DatabasePassword1!";
    private string _testDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private AzureBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator _orchestrator = null!;

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PasswordTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        // Initialize services
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _blobService = new AzureBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestDbPassword);
        _fileWatcherService = new FileWatcherService(_databaseService);
        
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            _chunkingService,
            _blobService,
            _fileWatcherService);
        
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        await _blobService.DisposeAsync();
        _encryptionService.Dispose();
        _databaseService.Dispose();
        _fileWatcherService.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Password Strength Tests

    [Theory]
    [InlineData("Short1!")]          // Too short (7 chars)
    [InlineData("shortshor1!")]      // 11 chars - still too short
    public async Task Initialize_WithShortPassword_ThrowsSecurityPolicyException(string weakPassword)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(() => 
            _orchestrator.InitializeAsync(weakPassword));
        
        Assert.Equal(SecurityPolicyType.WeakPassword, ex.PolicyType);
        Assert.Contains("12 characters", ex.Message);
    }

    [Theory]
    [InlineData("alllowercase1234")]  // Only lowercase and digits (2 types)
    [InlineData("ALLUPPERCASE1234")]  // Only uppercase and digits (2 types)
    [InlineData("NoDigitsHere!@")]    // Missing digits (3 types)
    [InlineData("!!!!!!!!!!!!")]      // Only special chars (1 type)
    [InlineData("MySecurePass123")]   // Missing special chars (3 types)
    [InlineData("mysecurepass1!")]    // Missing uppercase (3 types)
    [InlineData("MYSECUREPASS1!")]    // Missing lowercase (3 types)
    [InlineData("MySecurePass!@#")]   // Missing digits (3 types)
    public async Task Initialize_WithWeakCharacterMix_ThrowsSecurityPolicyException(string weakPassword)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(() => 
            _orchestrator.InitializeAsync(weakPassword));
        
        Assert.Equal(SecurityPolicyType.WeakPassword, ex.PolicyType);
        Assert.Contains("all of", ex.Message);
    }

    [Theory]
    [InlineData("StrongPassword1!")]     // Upper, lower, digit, special
    [InlineData("SECURE!pass123")]       // Upper, lower, digit, special
    [InlineData("Complex!Password1")]    // All 4 types
    [InlineData("MyP@ssw0rd1234")]       // All 4 types
    public async Task Initialize_WithStrongPassword_Succeeds(string strongPassword)
    {
        // Act
        var result = await _orchestrator.InitializeAsync(strongPassword);
        
        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task Initialize_ExistingUser_DoesNotValidatePasswordStrength()
    {
        // Arrange - First, create a user with a strong password
        await _orchestrator.InitializeAsync("InitialStrong1!");
        
        // Reset to simulate re-login scenario
        _encryptionService.ClearKey();
        
        // Create new orchestrator to simulate app restart
        await _orchestrator.DisposeAsync();
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            _chunkingService,
            _blobService,
            _fileWatcherService);
        
        // Act - Login with the same password (no validation for existing users)
        var result = await _orchestrator.InitializeAsync("InitialStrong1!");
        
        // Assert
        Assert.True(result);
    }

    #endregion

    #region Account Lockout Tests

    [Fact]
    public async Task Initialize_WrongPassword_IncrementsFailedAttempts()
    {
        // Arrange - Set up with a valid password first
        await _orchestrator.InitializeAsync("ValidPassword1!");
        _encryptionService.ClearKey();
        
        // Act - Try wrong password once
        var result = await _orchestrator.InitializeAsync("WrongPassword1!");
        
        // Assert
        Assert.False(result);
        var config = _databaseService.GetConfiguration();
        Assert.Equal(1, config.FailedLoginAttempts);
    }

    [Fact]
    public async Task Initialize_FiveWrongPasswords_SetsLockout()
    {
        // Arrange - Set up with a valid password first
        await _orchestrator.InitializeAsync("ValidPassword1!");
        _encryptionService.ClearKey();
        
        // Act - Try wrong password 5 times
        for (int i = 0; i < 5; i++)
        {
            var result = await _orchestrator.InitializeAsync("WrongPassword1!");
            Assert.False(result);
        }
        
        
        // Assert
        var config = _databaseService.GetConfiguration();
        Assert.Equal(5, config.FailedLoginAttempts);
        Assert.NotNull(config.LockoutUntilUtc);
    }

    [Fact]
    public async Task Initialize_AfterLockout_ThrowsSecurityPolicyException()
    {
        // Arrange - Set up with a valid password first
        await _orchestrator.InitializeAsync("ValidPassword1!");
        _encryptionService.ClearKey();
        
        // Set lockout directly using LockoutUntilUtc (stored as ticks to preserve UTC)
        var config = _databaseService.GetConfiguration();
        config.FailedLoginAttempts = 5;
        config.LockoutUntilUtc = DateTime.UtcNow.AddHours(1);
        _databaseService.SaveConfiguration(config);
        
        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(() => 
            _orchestrator.InitializeAsync("AnyPassword1!"));
        
        Assert.Equal(SecurityPolicyType.AccountLocked, ex.PolicyType);
    }

    #endregion
}
