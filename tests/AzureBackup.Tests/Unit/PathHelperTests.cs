using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for PathHelper utilities.
/// Uses platform-appropriate path separators via Path.Combine to work on Windows and Unix.
/// </summary>
public class PathHelperTests
{
    #region GetRelativePathFromBase

    [Fact]
    public void WhenFileUnderBaseThenReturnsRelativePath()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "sub", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal(Path.Combine("sub", "file.txt"), result);
    }

    [Fact]
    public void WhenFileDirectlyInBaseThenReturnsFilename()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void WhenFileNotUnderBaseThenFallsBackToFilename()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("D:", "Other", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void WhenBaseHasTrailingSeparatorThenStillWorks()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents") + Path.DirectorySeparatorChar;
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    #endregion

    #region FindCommonRoot

    [Fact]
    public void WhenAllPathsShareRootThenReturnsCommonDirectory()
    {
        var paths = new[]
        {
            Path.Combine("C:", "Users", "me", "Docs", "a.txt"),
            Path.Combine("C:", "Users", "me", "Docs", "b.txt"),
            Path.Combine("C:", "Users", "me", "Docs", "sub", "c.txt")
        };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me", "Docs"), result);
    }

    [Fact]
    public void WhenPathsInDifferentSubfoldersThenReturnsParent()
    {
        var paths = new[]
        {
            Path.Combine("C:", "Users", "me", "Docs", "a.txt"),
            Path.Combine("C:", "Users", "me", "Photos", "b.jpg")
        };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me"), result);
    }

    [Fact]
    public void WhenEmptyListThenReturnsEmpty()
    {
        var result = PathHelper.FindCommonRoot(Array.Empty<string>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhenSinglePathThenReturnsItsDirectory()
    {
        var paths = new[] { Path.Combine("C:", "Users", "me", "file.txt") };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me"), result);
    }

    #endregion

    #region GetDisplayName

    [Theory]
    [InlineData("Documents", "Documents")]
    [InlineData("my-folder", "my-folder")]
    public void WhenSimpleFolderNameThenReturnsName(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.GetDisplayName(input));
    }

    [Fact]
    public void WhenNestedPathThenReturnsLastSegment()
    {
        var path = Path.Combine("C:", "Users", "me", "Documents");
        Assert.Equal("Documents", PathHelper.GetDisplayName(path));
    }

    [Fact]
    public void WhenPathHasTrailingSeparatorThenReturnsLastSegment()
    {
        var path = Path.Combine("C:", "Users", "me", "Documents") + Path.DirectorySeparatorChar;
        Assert.Equal("Documents", PathHelper.GetDisplayName(path));
    }

    [Fact]
    public void WhenDriveRootThenReturnsDriveWithSeparator()
    {
        // On Windows, Path.GetFileName("C:") returns "" so GetDisplayName adds the separator back
        var result = PathHelper.GetDisplayName("C:" + Path.DirectorySeparatorChar);

        // Should return something like "C:\" on Windows
        Assert.False(string.IsNullOrEmpty(result));
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), result);
    }

    #endregion
}
