using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for FileSystemHelper directory cleanup utilities.
/// </summary>
public class FileSystemHelperTests : IDisposable
{
    private readonly string _testRoot;

    public FileSystemHelperTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"FileSystemHelperTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void WhenEmptySubdirectoriesThenDeletesThem()
    {
        var emptyDir = Path.Combine(_testRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.False(Directory.Exists(emptyDir));
    }

    [Fact]
    public void WhenNestedEmptyDirectoriesThenDeletesAllLevels()
    {
        var nested = Path.Combine(_testRoot, "a", "b", "c");
        Directory.CreateDirectory(nested);

        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.False(Directory.Exists(Path.Combine(_testRoot, "a")));
    }

    [Fact]
    public void WhenDirectoryContainsFileThenPreservesIt()
    {
        var dirWithFile = Path.Combine(_testRoot, "has-file");
        Directory.CreateDirectory(dirWithFile);
        File.WriteAllText(Path.Combine(dirWithFile, "keep.txt"), "data");

        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.True(Directory.Exists(dirWithFile));
    }

    [Fact]
    public void WhenMixOfEmptyAndNonEmptyThenDeletesOnlyEmpty()
    {
        var emptyDir = Path.Combine(_testRoot, "empty");
        var fullDir = Path.Combine(_testRoot, "full");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(fullDir);
        File.WriteAllText(Path.Combine(fullDir, "file.txt"), "data");

        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.False(Directory.Exists(emptyDir));
        Assert.True(Directory.Exists(fullDir));
    }

    [Fact]
    public void WhenRootIsEmptyThenDoesNotDeleteRoot()
    {
        // Root itself should never be deleted, only subdirectories
        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.True(Directory.Exists(_testRoot));
    }

    [Fact]
    public void WhenDeepNestedWithFileAtLeafThenPreservesEntireChain()
    {
        var deepPath = Path.Combine(_testRoot, "a", "b", "c");
        Directory.CreateDirectory(deepPath);
        File.WriteAllText(Path.Combine(deepPath, "leaf.txt"), "data");

        FileSystemHelper.CleanEmptyDirectories(_testRoot);

        Assert.True(Directory.Exists(deepPath));
    }
}
