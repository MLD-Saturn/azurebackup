using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for GlobMatcher wildcard pattern matching.
/// </summary>
public class GlobMatcherTests
{
    #region IsMatch — Star wildcard

    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "FILE.TXT", true)]
    [InlineData("*.txt", "file.doc", false)]
    [InlineData("*.log", "debug.log", true)]
    [InlineData("temp*", "tempfile", true)]
    [InlineData("temp*", "temporary.bak", true)]
    [InlineData("temp*", "nottemp", false)]
    [InlineData("~*", "~tempfile", true)]
    [InlineData("~*", "normal_file", false)]
    public void WhenStarWildcardUsedThenMatchesZeroOrMoreCharacters(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(input, pattern));
    }

    #endregion

    #region IsMatch — Question mark wildcard

    [Theory]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "fileA.txt", true)]
    [InlineData("file?.txt", "file.txt", false)]
    [InlineData("file?.txt", "file12.txt", false)]
    [InlineData("???.dat", "abc.dat", true)]
    [InlineData("???.dat", "ab.dat", false)]
    [InlineData("???.dat", "abcd.dat", false)]
    public void WhenQuestionMarkUsedThenMatchesExactlyOneCharacter(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(input, pattern));
    }

    #endregion

    #region IsMatch — Exact match

    [Theory]
    [InlineData("thumbs.db", "thumbs.db", true)]
    [InlineData("thumbs.db", "Thumbs.db", true)]
    [InlineData("thumbs.db", "thumbs.db.bak", false)]
    [InlineData("desktop.ini", "desktop.ini", true)]
    [InlineData(".DS_Store", ".DS_Store", true)]
    public void WhenNoWildcardsThenMatchesExactNameCaseInsensitive(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(input, pattern));
    }

    #endregion

    #region IsMatch — Edge cases

    [Theory]
    [InlineData("", "file.txt", false)]
    [InlineData("*.txt", "", false)]
    [InlineData("", "", false)]
    [InlineData(null, "file.txt", false)]
    [InlineData("*.txt", null, false)]
    public void WhenInputOrPatternEmptyOrNullThenReturnsFalse(string? pattern, string? input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(input!, pattern!));
    }

    [Fact]
    public void WhenStarOnlyThenMatchesAnything()
    {
        Assert.True(GlobMatcher.IsMatch("anything.txt", "*"));
        Assert.True(GlobMatcher.IsMatch("x", "*"));
    }

    [Theory]
    [InlineData("file[1].txt", "file[1].txt", true)]
    [InlineData("file(1).txt", "file(1).txt", true)]
    [InlineData("file+data.txt", "file+data.txt", true)]
    public void WhenPatternContainsRegexMetaCharsThenEscapedCorrectly(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(input, pattern));
    }

    #endregion

    #region MatchesAny

    [Fact]
    public void WhenNameMatchesOnePatternThenReturnsTrue()
    {
        var patterns = new[] { "*.doc", "*.txt", "*.pdf" };
        Assert.True(GlobMatcher.MatchesAny("readme.txt", patterns));
    }

    [Fact]
    public void WhenNameMatchesNoPatternThenReturnsFalse()
    {
        var patterns = new[] { "*.doc", "*.txt", "*.pdf" };
        Assert.False(GlobMatcher.MatchesAny("image.png", patterns));
    }

    [Fact]
    public void WhenPatternsContainWhitespaceEntriesThenSkipsThem()
    {
        var patterns = new[] { "", " ", "  ", "*.txt" };
        Assert.True(GlobMatcher.MatchesAny("file.txt", patterns));
        Assert.False(GlobMatcher.MatchesAny("file.doc", patterns));
    }

    #endregion
}
