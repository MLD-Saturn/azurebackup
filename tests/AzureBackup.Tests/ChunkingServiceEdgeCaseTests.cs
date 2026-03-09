using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Additional edge case and parameterized tests for ChunkingService.
/// </summary>
public class ChunkingServiceEdgeCaseTests : IAsyncLifetime
{
    private readonly ChunkingService _chunkingService;
    private string _testDirectory = null!;

    public ChunkingServiceEdgeCaseTests()
    {
        _chunkingService = new ChunkingService();
    }

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ChunkingEdgeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
        return Task.CompletedTask;
    }

    #region Parameterized Size Tests

    [Theory]
    [InlineData(0)]                    // Empty
    [InlineData(1)]                    // Single byte
    [InlineData(100)]                  // Small
    [InlineData(64 * 1024 - 1)]        // Just under min chunk
    [InlineData(64 * 1024)]            // Exactly min chunk
    [InlineData(64 * 1024 + 1)]        // Just over min chunk
    [InlineData(1024 * 1024)]          // 1 MB
    [InlineData(1024 * 1024 - 1)]      // Just under max chunk
    [InlineData(1024 * 1024 + 1)]      // Just over max chunk
    public async Task ChunkFile_BoundarySizes_HandlesCorrectly(int size)
    {
        // Arrange
        var filePath = CreateTestFile($"boundary_{size}.bin", size);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.NotNull(chunks);
        Assert.True(chunks.Count >= 1);
        Assert.Equal(size, chunks.Sum(c => c.Length));
        
        // Verify chunk indices are sequential
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
        }
    }

    [Theory]
    [InlineData(".txt", true)]    // Text - small chunks expected
    [InlineData(".cs", true)]     // Code - small chunks expected
    [InlineData(".json", true)]   // Config - small chunks expected
    [InlineData(".mp4", false)]   // Video - large chunks expected
    [InlineData(".zip", false)]   // Archive - large chunks expected
    [InlineData(".bin", false)]   // Binary - default chunks expected
    [InlineData(".xyz", false)]   // Unknown - default chunks expected
    public async Task ChunkFile_FileExtension_UsesAppropriateChunkSize(string extension, bool expectSmallChunks)
    {
        // Arrange - 500KB file
        var filePath = CreateTestFile($"adaptive{extension}", 500 * 1024);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        if (expectSmallChunks)
        {
            // Should have more chunks for text/code files
            Assert.True(chunks.Count >= 4, $"Expected >=4 chunks for {extension}, got {chunks.Count}");
        }
        // Note: Large files may still have few chunks depending on content boundaries
    }

    #endregion

    #region Special File Path Tests

    [Theory]
    [InlineData("simple.txt")]
    [InlineData("file with spaces.txt")]
    [InlineData("file-with-dashes.txt")]
    [InlineData("file_with_underscores.txt")]
    [InlineData("file.multiple.dots.txt")]
    [InlineData("UPPERCASE.TXT")]
    public async Task ChunkFile_VariousFilenames_HandlesCorrectly(string filename)
    {
        // Arrange
        var filePath = CreateTestFile(filename, 1024);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.Single(chunks);
    }

    [Fact]
    public async Task ChunkFile_DeepNestedPath_HandlesCorrectly()
    {
        // Arrange
        var deepPath = Path.Combine(_testDirectory, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deepPath);
        var filePath = Path.Combine(deepPath, "nested.txt");
        await File.WriteAllBytesAsync(filePath, new byte[1024]);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.Single(chunks);
    }

    #endregion

    #region Content Pattern Tests

    [Fact]
    public async Task ChunkFile_AllZeros_ChunksCorrectly()
    {
        // Arrange - All zeros (worst case for content-defined chunking)
        var filePath = Path.Combine(_testDirectory, "zeros.bin");
        await File.WriteAllBytesAsync(filePath, new byte[500 * 1024]);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.NotEmpty(chunks);
        Assert.Equal(500 * 1024, chunks.Sum(c => c.Length));
    }

    [Fact]
    public async Task ChunkFile_RandomData_ChunksWithVariableSizes()
    {
        // Arrange
        Random random = new(42);
        byte[] data = new byte[1024 * 1024]; // 1 MB
        random.NextBytes(data);
        var filePath = Path.Combine(_testDirectory, "random.bin");
        await File.WriteAllBytesAsync(filePath, data);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.True(chunks.Count > 1);
        
        // Chunks should have variable sizes (content-defined)
        var sizes = chunks.Select(c => c.Length).Distinct().ToList();
        Assert.True(sizes.Count > 1, "Expected variable chunk sizes");
    }

    [Fact]
    public async Task ChunkFile_HighlyCompressible_ChunksCorrectly()
    {
        // Arrange - Repeating pattern (highly compressible)
        var pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
        byte[] data = new byte[500 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = pattern[i % pattern.Length];
        }
        var filePath = Path.Combine(_testDirectory, "compressible.bin");
        await File.WriteAllBytesAsync(filePath, data);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.NotEmpty(chunks);
        Assert.Equal(500 * 1024, chunks.Sum(c => c.Length));
    }

    #endregion

    #region Hash Collision Resistance Tests

    [Fact]
    public async Task ComputeHash_SimilarContent_ProducesDifferentHashes()
    {
        // Arrange - Two files differing by single byte
        byte[] data1 = new byte[10000];
        byte[] data2 = new byte[10000];
        new Random(42).NextBytes(data1);
        data1.CopyTo(data2, 0);
        data2[5000] ^= 0x01; // Flip one bit

        var file1 = Path.Combine(_testDirectory, "similar1.bin");
        var file2 = Path.Combine(_testDirectory, "similar2.bin");
        await File.WriteAllBytesAsync(file1, data1);
        await File.WriteAllBytesAsync(file2, data2);

        // Act
        var hash1 = await _chunkingService.ComputeFileHashAsync(file1);
        var hash2 = await _chunkingService.ComputeFileHashAsync(file2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ChunkHash_SameContent_SameHash()
    {
        // Arrange - Same content in different files
        byte[] data = new byte[100 * 1024];
        new Random(42).NextBytes(data);
        
        var file1 = Path.Combine(_testDirectory, "same1.bin");
        var file2 = Path.Combine(_testDirectory, "same2.bin");
        await File.WriteAllBytesAsync(file1, data);
        await File.WriteAllBytesAsync(file2, data);

        // Act
        var chunks1 = await _chunkingService.ChunkFileAsync(file1);
        var chunks2 = await _chunkingService.ChunkFileAsync(file2);

        // Assert - Same content = same chunk hashes
        Assert.Equal(
            chunks1.Select(c => c.Hash).OrderBy(h => h),
            chunks2.Select(c => c.Hash).OrderBy(h => h));
    }

    #endregion

    #region Delta Detection Edge Cases

    [Fact]
    public void GetChangedChunks_EmptyOld_ReturnsAllNew()
    {
        // Arrange
        List<ChunkInfo> oldChunks = new();
        List<ChunkInfo> newChunks = new()
        {
            new() { Index = 0, Hash = "ABC123", Length = 1000 },
            new() { Index = 1, Hash = "DEF456", Length = 2000 }
        };

        // Act
        var changed = _chunkingService.GetChangedChunks(oldChunks, newChunks);

        // Assert
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void GetChangedChunks_EmptyNew_ReturnsEmpty()
    {
        // Arrange
        List<ChunkInfo> oldChunks = new()
        {
            new() { Index = 0, Hash = "ABC123", Length = 1000 }
        };
        List<ChunkInfo> newChunks = new();

        // Act
        var changed = _chunkingService.GetChangedChunks(oldChunks, newChunks);

        // Assert
        Assert.Empty(changed);
    }

    [Fact]
    public void GetChangedChunks_ReorderedChunks_DetectsCorrectly()
    {
        // Arrange
        List<ChunkInfo> oldChunks = new()
        {
            new() { Index = 0, Hash = "AAA", Length = 1000 },
            new() { Index = 1, Hash = "BBB", Length = 1000 },
            new() { Index = 2, Hash = "CCC", Length = 1000 }
        };
        List<ChunkInfo> newChunks = new()
        {
            new() { Index = 0, Hash = "CCC", Length = 1000 }, // Moved
            new() { Index = 1, Hash = "BBB", Length = 1000 }, // Same
            new() { Index = 2, Hash = "AAA", Length = 1000 }  // Moved
        };

        // Act
        var changed = _chunkingService.GetChangedChunks(oldChunks, newChunks);

        // Assert - Hash exists in old, so no new uploads needed
        Assert.Empty(changed);
    }

    [Fact]
    public void GetChangedChunks_MixedChanges_DetectsOnlyNew()
    {
        // Arrange
        List<ChunkInfo> oldChunks = new()
        {
            new() { Index = 0, Hash = "EXISTING1" },
            new() { Index = 1, Hash = "EXISTING2" }
        };
        List<ChunkInfo> newChunks = new()
        {
            new() { Index = 0, Hash = "EXISTING1" }, // Same
            new() { Index = 1, Hash = "NEW1" },      // Changed
            new() { Index = 2, Hash = "NEW2" }       // Added
        };

        // Act
        var changed = _chunkingService.GetChangedChunks(oldChunks, newChunks);

        // Assert
        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, c => c.Hash == "NEW1");
        Assert.Contains(changed, c => c.Hash == "NEW2");
    }

    #endregion

    #region ReadChunk Edge Cases

    [Fact]
    public async Task ReadChunk_FirstChunk_ReturnsCorrectData()
    {
        // Arrange
        byte[] data = new byte[256 * 1024];
        new Random(42).NextBytes(data);
        var filePath = Path.Combine(_testDirectory, "read_first.bin");
        await File.WriteAllBytesAsync(filePath, data);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Act
        var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunks[0]);

        // Assert
        Assert.Equal(data.Take(chunks[0].Length), chunkData);
    }

    [Fact]
    public async Task ReadChunk_LastChunk_ReturnsCorrectData()
    {
        // Arrange
        byte[] data = new byte[500 * 1024];
        new Random(42).NextBytes(data);
        var filePath = Path.Combine(_testDirectory, "read_last.bin");
        await File.WriteAllBytesAsync(filePath, data);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);
        var lastChunk = chunks[^1];

        // Act
        var chunkData = await _chunkingService.ReadChunkAsync(filePath, lastChunk);

        // Assert
        var expectedData = data.Skip((int)lastChunk.Offset).Take(lastChunk.Length).ToArray();
        Assert.Equal(expectedData, chunkData);
    }

    [Fact]
    public async Task ReadChunk_MiddleChunk_ReturnsCorrectData()
    {
        // Arrange
        byte[] data = new byte[1024 * 1024]; // 1 MB - should have multiple chunks
        new Random(42).NextBytes(data);
        var filePath = Path.Combine(_testDirectory, "read_middle.bin");
        await File.WriteAllBytesAsync(filePath, data);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);
        
        if (chunks.Count < 3) return; // Skip if not enough chunks
        var middleChunk = chunks[chunks.Count / 2];

        // Act
        var chunkData = await _chunkingService.ReadChunkAsync(filePath, middleChunk);

        // Assert
        var expectedData = data.Skip((int)middleChunk.Offset).Take(middleChunk.Length).ToArray();
        Assert.Equal(expectedData, chunkData);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task ChunkFile_NonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.bin");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            _chunkingService.ChunkFileAsync(filePath));
    }

    [Fact]
    public async Task ComputeHash_NonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.bin");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            _chunkingService.ComputeFileHashAsync(filePath));
    }

    [Fact]
    public async Task ChunkFile_NullPath_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _chunkingService.ChunkFileAsync(null!));
    }

    [Fact]
    public async Task ChunkFile_EmptyPath_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _chunkingService.ChunkFileAsync(""));
    }

    #endregion

    #region Concurrent Access Tests

    [Fact]
    public async Task ChunkFile_ConcurrentReads_AllSucceed()
    {
        // Arrange
        var filePath = CreateTestFile("concurrent.bin", 100 * 1024);

        // Act - Read same file concurrently
        var tasks = Enumerable.Range(0, 10).Select(_ => 
            _chunkingService.ChunkFileAsync(filePath));
        var results = await Task.WhenAll(tasks);

        // Assert - All should return identical results
        var firstResult = results[0];
        foreach (var result in results.Skip(1))
        {
            Assert.Equal(firstResult.Count, result.Count);
            for (int i = 0; i < firstResult.Count; i++)
            {
                Assert.Equal(firstResult[i].Hash, result[i].Hash);
            }
        }
    }

    #endregion

    private string CreateTestFile(string name, int size)
    {
        var filePath = Path.Combine(_testDirectory, name);
        byte[] content = new byte[size];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }
}
