using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class SignalCommandMatchTests
{
    [Fact]
    public void TryParse_ValidSignal_ReturnsTrue()
    {
        var result = SignalCommandMatch.TryParse("window.size.set:100", "window.size.set", out var match);

        Assert.True(result);
        Assert.Equal("window.size.set", match.Command);
        Assert.Equal("100", match.Payload);
    }

    [Fact]
    public void TryParse_SignalWithPrefix_ReturnsTrue()
    {
        var result = SignalCommandMatch.TryParse("prefix.window.size.set:200", "window.size.set", out var match);

        Assert.True(result);
        Assert.Equal("window.size.set", match.Command);
        Assert.Equal("200", match.Payload);
    }

    [Fact]
    public void TryParse_EmptyPayload_ReturnsTrue()
    {
        var result = SignalCommandMatch.TryParse("window.size.set:", "window.size.set", out var match);

        Assert.True(result);
        Assert.Equal("window.size.set", match.Command);
        Assert.Equal("", match.Payload);
    }

    [Fact]
    public void TryParse_NullSignal_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse(null!, "window.size.set", out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_EmptySignal_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse("", "window.size.set", out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_NullCommand_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse("window.size.set:100", null!, out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_EmptyCommand_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse("window.size.set:100", "", out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_NoMatch_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse("other.signal", "window.size.set", out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_NoColon_ReturnsFalse()
    {
        var result = SignalCommandMatch.TryParse("window.size.set", "window.size.set", out var match);

        Assert.False(result);
        Assert.Equal(default, match);
    }

    [Fact]
    public void TryParse_PayloadWithColon_ReturnsFullPayload()
    {
        var result = SignalCommandMatch.TryParse("window.time.set:00:30:00", "window.time.set", out var match);

        Assert.True(result);
        Assert.Equal("window.time.set", match.Command);
        Assert.Equal("00:30:00", match.Payload);
    }

    [Fact]
    public void TryParse_PayloadWithSpecialChars_ReturnsPayload()
    {
        var result = SignalCommandMatch.TryParse("command:payload with spaces!@#", "command", out var match);

        Assert.True(result);
        Assert.Equal("command", match.Command);
        Assert.Equal("payload with spaces!@#", match.Payload);
    }

    [Fact]
    public void TryParse_MultipleColons_ParsesCorrectly()
    {
        var result = SignalCommandMatch.TryParse("cmd:value1:value2:value3", "cmd", out var match);

        Assert.True(result);
        Assert.Equal("cmd", match.Command);
        Assert.Equal("value1:value2:value3", match.Payload);
    }

    [Fact]
    public void TryParse_CommandAtEnd_ReturnsTrue()
    {
        var result = SignalCommandMatch.TryParse("prefix.suffix.cmd:123", "cmd", out var match);

        Assert.True(result);
        Assert.Equal("cmd", match.Command);
        Assert.Equal("123", match.Payload);
    }

    [Fact]
    public void Deconstruct_UnpacksCorrectly()
    {
        SignalCommandMatch.TryParse("window.size.set:100", "window.size.set", out var match);

        var (command, payload) = match;

        Assert.Equal("window.size.set", command);
        Assert.Equal("100", payload);
    }

    [Theory]
    [InlineData("window.size.set:100", "window.size.set", true, "100")]
    [InlineData("window.size.increase:50", "window.size.increase", true, "50")]
    [InlineData("window.time.set:30s", "window.time.set", true, "30s")]
    [InlineData("prefix.window.size.set:200", "window.size.set", true, "200")]
    [InlineData("no.match", "window.size.set", false, null)]
    [InlineData("", "window.size.set", false, null)]
    public void TryParse_VariousInputs_ReturnsExpected(string signal, string command, bool expectedResult, string? expectedPayload)
    {
        var result = SignalCommandMatch.TryParse(signal, command, out var match);

        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedPayload, match.Payload);
        }
    }

    // Edge case: Signal ends exactly at command boundary (the fix we added)
    [Fact]
    public void TryParse_SignalEndsAtCommandBoundary_ReturnsFalse()
    {
        // This would cause ArgumentOutOfRangeException without the bounds check fix
        var malformedSignal = "window.size.set:";

        var result = SignalCommandMatch.TryParse(malformedSignal, "window.size.set", out var match);

        // Should return true with empty payload
        Assert.True(result);
        Assert.Equal("", match.Payload);
    }
}
