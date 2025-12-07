using Mostlylucid.Helpers.Ephemeral;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class StringPatternMatcherTests
{
    [Theory]
    [InlineData("signal-ready", "signal-ready", true)]
    [InlineData("signal-ready", "signal-*", true)]
    [InlineData("signal-ready", "*ready", true)]
    [InlineData("signal-ready", "signal-?eady", true)]
    [InlineData("signal-ready", "signal-?ead*", true)]
    [InlineData("signal-ready", "other-*", false)]
    [InlineData("ready", "re*y", true)]
    [InlineData("ready", "re?dy", true)]
    [InlineData("ready", "re??y", true)]
    public void Matches_Wildcards(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, StringPatternMatcher.Matches(value, pattern));
    }

    [Theory]
    [InlineData("anything", "*", true)]
    [InlineData("anything", "*,*", true)]
    [InlineData("anything", "foo, * ,bar", true)]
    [InlineData("anything", "foo,,bar", false)]
    public void Matches_CommaAndCatchAll(string value, string pattern, bool expected)
    {
        var patterns = new HashSet<string> { pattern };
        Assert.Equal(expected, StringPatternMatcher.MatchesAny(value, patterns));
    }

    [Fact]
    public void MatchesAny_CommaSeparated()
    {
        var patterns = new HashSet<string> { "foo,bar-*,baz" };

        Assert.True(StringPatternMatcher.MatchesAny("bar-hot", patterns));
        Assert.True(StringPatternMatcher.MatchesAny("foo", patterns));
        Assert.False(StringPatternMatcher.MatchesAny("qux", patterns));
    }
}
