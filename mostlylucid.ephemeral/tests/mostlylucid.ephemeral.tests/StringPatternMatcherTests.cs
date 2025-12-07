using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class StringPatternMatcherTests
{
    [Theory]
    [InlineData("hello", "hello", true)]
    [InlineData("hello", "world", false)]
    [InlineData("hello", "he*", true)]
    [InlineData("hello", "*lo", true)]
    [InlineData("hello", "*ll*", true)]
    [InlineData("hello", "h?llo", true)]
    [InlineData("hello", "h?lo", false)]
    [InlineData("error.timeout", "error.*", true)]
    [InlineData("error.timeout", "*.timeout", true)]
    [InlineData("error.database.timeout", "error.*.timeout", true)]
    [InlineData("error.database.timeout", "error.*", true)]
    [InlineData("warning.low", "error.*", false)]
    public void Matches_GlobPatterns(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, StringPatternMatcher.Matches(value, pattern));
    }

    [Fact]
    public void MatchesAny_WithNullPatterns_ReturnsFalse()
    {
        Assert.False(StringPatternMatcher.MatchesAny("test", null));
    }

    [Fact]
    public void MatchesAny_WithEmptyPatterns_ReturnsFalse()
    {
        Assert.False(StringPatternMatcher.MatchesAny("test", new HashSet<string>()));
    }

    [Fact]
    public void MatchesAny_WithMatchingPattern_ReturnsTrue()
    {
        var patterns = new HashSet<string> { "error.*", "warning.*" };
        Assert.True(StringPatternMatcher.MatchesAny("error.timeout", patterns));
        Assert.True(StringPatternMatcher.MatchesAny("warning.low", patterns));
        Assert.False(StringPatternMatcher.MatchesAny("info.message", patterns));
    }

}
