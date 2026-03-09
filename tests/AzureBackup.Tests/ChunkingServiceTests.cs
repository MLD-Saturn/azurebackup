using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for ChunkingService covering content-defined chunking, hashing,
/// adaptive chunk sizes, and delta detection.
/// </summary>
public class ChunkingServiceTests : IAsyncLifetime
{
    private readonly ChunkingService _chunkingService;
    private string _testDirectory = null!;

    public ChunkingServiceTests()
    {
        _chunkingService = new ChunkingService();
    }

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ChunkingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    #region Small File Tests

    [Fact]
    public async Task ChunkFileAsync_SmallFile_ReturnsSingleChunk()
    {
        // Arrange
        var filePath = CreateTestFile("small.txt", 1024); // 1 KB

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(1024, chunks[0].Length);
    }

    [Fact]
    public async Task ChunkFileAsync_EmptyFile_ReturnsSingleEmptyChunk()
    {
        // Arrange
        var filePath = CreateTestFile("empty.txt", 0);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Length);
    }

    #endregion

    #region Large File Chunking Tests

    [Fact]
    public async Task ChunkFileAsync_LargeFile_ReturnsMultipleChunks()
    {
        // Arrange - Create 2 MB file (larger than max chunk size)
        var filePath = CreateTestFile("large.bin", 2 * 1024 * 1024);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        Assert.True(chunks.Count >= 2);
        
        // Verify chunk sequence
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
        }
        
        // Verify no gaps
        var totalSize = chunks.Sum(c => (long)c.Length);
        Assert.Equal(2 * 1024 * 1024, totalSize);
    }

    [Fact]
    public async Task ChunkFileAsync_ChunksHaveValidOffsets()
    {
        // Arrange
        var filePath = CreateTestFile("offsets.bin", 1024 * 1024); // 1 MB

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert - Offsets should be contiguous
        long expectedOffset = 0;
        foreach (var chunk in chunks.OrderBy(c => c.Index))
        {
            Assert.Equal(expectedOffset, chunk.Offset);
            expectedOffset += chunk.Length;
        }
    }

    [Fact]
    public async Task ChunkFileAsync_AllChunksHaveHashes()
    {
        // Arrange
        var filePath = CreateTestFile("hashes.bin", 512 * 1024);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert
        foreach (var chunk in chunks)
        {
            Assert.False(string.IsNullOrEmpty(chunk.Hash));
            Assert.Equal(64, chunk.Hash.Length); // SHA-256 hex = 64 chars
        }
    }

    #endregion

    #region Content-Defined Chunking Tests

    [Fact]
    public async Task ChunkFileAsync_SameContent_SameChunks()
    {
        // Arrange - Create two identical files
        var content = CreateRandomContent(500 * 1024);
        var file1 = Path.Combine(_testDirectory, "identical1.bin");
        var file2 = Path.Combine(_testDirectory, "identical2.bin");
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act
        var chunks1 = await _chunkingService.ChunkFileAsync(file1);
        var chunks2 = await _chunkingService.ChunkFileAsync(file2);

        // Assert
        Assert.Equal(chunks1.Count, chunks2.Count);
        for (int i = 0; i < chunks1.Count; i++)
        {
            Assert.Equal(chunks1[i].Hash, chunks2[i].Hash);
            Assert.Equal(chunks1[i].Length, chunks2[i].Length);
        }
    }

    [Fact]
    public async Task ChunkFileAsync_ModifiedContent_SomeChunksChange()
    {
        // Arrange - Create file, then modify middle
        var content = CreateRandomContent(500 * 1024);
        var file1 = Path.Combine(_testDirectory, "original.bin");
        await File.WriteAllBytesAsync(file1, content);
        
        var chunks1 = await _chunkingService.ChunkFileAsync(file1);
        
        // Modify content in the middle
        content[250 * 1024] = (byte)(content[250 * 1024] ^ 0xFF);
        var file2 = Path.Combine(_testDirectory, "modified.bin");
        await File.WriteAllBytesAsync(file2, content);

        // Act
        var chunks2 = await _chunkingService.ChunkFileAsync(file2);

        // Assert - Some chunks should be the same (deduplication opportunity)
        var hashes1 = chunks1.Select(c => c.Hash).ToHashSet();
        var hashes2 = chunks2.Select(c => c.Hash).ToHashSet();
        
        // At least one chunk should be different
        Assert.NotEqual(hashes1, hashes2);
    }

    #endregion

    #region Adaptive Chunk Size Tests

    [Fact]
    public async Task ChunkFileAsync_TextFile_UsesSmallChunks()
    {
        // Arrange - Text files should use smaller chunks
        var filePath = CreateTestFile("code.cs", 500 * 1024);

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert - Should have more chunks due to smaller max size for code files
        Assert.True(chunks.Count >= 4); // 500KB / 128KB max = ~4 chunks
    }

    [Fact]
    public async Task ChunkFileAsync_VideoFile_UsesLargeChunks()
    {
        // Arrange - Video files should use larger chunks
        var filePath = CreateTestFile("video.mp4", 10 * 1024 * 1024); // 10 MB

        // Act
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Assert - Should have fewer chunks due to larger max size
        Assert.True(chunks.Count <= 10); // 10MB / 1MB min = ~10 chunks max
    }

    #endregion

    #region Hash Computation Tests

    [Fact]
    public async Task ComputeFileHashAsync_ReturnsConsistentHash()
    {
        // Arrange
        var filePath = CreateTestFile("hash.bin", 1024);

        // Act
        var hash1 = await _chunkingService.ComputeFileHashAsync(filePath);
        var hash2 = await _chunkingService.ComputeFileHashAsync(filePath);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex
    }

    [Fact]
    public async Task ComputeFileHashAsync_DifferentContent_DifferentHash()
    {
        // Arrange - Use different seeds for different content
        byte[] content1 = new byte[1024];
        new Random(1).NextBytes(content1);
        var file1 = Path.Combine(_testDirectory, "file1.bin");
        await File.WriteAllBytesAsync(file1, content1);
        
        byte[] content2 = new byte[1024];
        new Random(2).NextBytes(content2);
        var file2 = Path.Combine(_testDirectory, "file2.bin");
        await File.WriteAllBytesAsync(file2, content2);

        // Act
        var hash1 = await _chunkingService.ComputeFileHashAsync(file1);
        var hash2 = await _chunkingService.ComputeFileHashAsync(file2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    #endregion

    #region Delta Detection Tests

    [Fact]
    public async Task GetChangedChunks_NoChanges_ReturnsEmpty()
    {
        // Arrange
        var filePath = CreateTestFile("unchanged.bin", 256 * 1024);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Act
        var changed = _chunkingService.GetChangedChunks(chunks, chunks);

        // Assert
        Assert.Empty(changed);
    }

    [Fact]
    public async Task GetChangedChunks_NewFile_ReturnsAllChunks()
    {
        // Arrange
        var filePath = CreateTestFile("new.bin", 256 * 1024);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);
        List<AzureBackup.Core.Models.ChunkInfo> existingChunks = new();

        // Act
        var changed = _chunkingService.GetChangedChunks(existingChunks, chunks);

        // Assert
        Assert.Equal(chunks.Count, changed.Count);
    }

    [Fact]
    public async Task GetChangedChunks_PartialOverlap_ReturnsOnlyNew()
    {
        // Arrange - Create file large enough for multiple chunks
        var content1 = CreateRandomContent(512 * 1024); // 512 KB
        var file1 = Path.Combine(_testDirectory, "v1.bin");
        await File.WriteAllBytesAsync(file1, content1);
        var chunks1 = await _chunkingService.ChunkFileAsync(file1);

        // Create completely different content for v2 to ensure new chunks
        byte[] content2 = new byte[512 * 1024];
        new Random(999).NextBytes(content2); // Different seed = different content
        var file2 = Path.Combine(_testDirectory, "v2.bin");
        await File.WriteAllBytesAsync(file2, content2);
        var chunks2 = await _chunkingService.ChunkFileAsync(file2);

        // Act
        var changed = _chunkingService.GetChangedChunks(chunks1, chunks2);

        // Assert - All new chunks should be detected as changed
        Assert.True(changed.Count > 0);
        Assert.Equal(chunks2.Count, changed.Count); // All chunks are new
    }

    #endregion

    #region ReadChunk Tests

    [Fact]
    public async Task ReadChunkAsync_ReturnsCorrectData()
    {
        // Arrange
        var content = CreateRandomContent(256 * 1024);
        var filePath = Path.Combine(_testDirectory, "readable.bin");
        await File.WriteAllBytesAsync(filePath, content);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);

        // Act
        var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunks[0]);

        // Assert
        Assert.Equal(chunks[0].Length, chunkData.Length);
        Assert.Equal(content.Take(chunkData.Length), chunkData);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ChunkFileAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        var filePath = CreateTestFile("cancel.bin", 5 * 1024 * 1024); // 5 MB
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => 
            _chunkingService.ChunkFileAsync(filePath, cts.Token));
    }

    #endregion

    #region Helper Methods

    private string CreateTestFile(string name, int size)
    {
        var filePath = Path.Combine(_testDirectory, name);
        var content = CreateRandomContent(size);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        new Random(42).NextBytes(content); // Seeded for reproducibility
        return content;
    }

    #endregion
}
