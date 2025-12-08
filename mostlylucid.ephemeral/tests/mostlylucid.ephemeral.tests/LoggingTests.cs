using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral.Logging;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class LoggingTests
{
    [Fact]
    public void SignalLoggerProvider_CreatesLogger()
    {
        var sink = new SignalSink();
        using var provider = new SignalLoggerProvider(sink);

        var logger = provider.CreateLogger("TestCategory");

        Assert.NotNull(logger);
    }

    [Fact]
    public void SignalLogger_RaisesSignalOnLog()
    {
        var sink = new SignalSink();
        using var provider = new SignalLoggerProvider(sink);
        var logger = provider.CreateLogger("MyApp.Service");

        logger.LogInformation(new EventId(100, "UserLogin"), "User logged in");

        Assert.True(sink.Detect(s => s.Signal.StartsWith("log.information")));
        var signals = sink.Sense(s => s.Signal.Contains("userlogin"));
        Assert.NotEmpty(signals);
    }

    [Fact]
    public void SignalLogger_RespectsMinimumLevel()
    {
        var sink = new SignalSink();
        var options = new SignalLogHookOptions { MinimumLevel = LogLevel.Warning };
        using var provider = new SignalLoggerProvider(sink, options);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("Should be ignored");
        logger.LogWarning("Should be captured");

        Assert.False(sink.Detect(s => s.Signal.Contains("information")));
        Assert.True(sink.Detect(s => s.Signal.Contains("warning")));
    }

    [Fact]
    public void SignalLogger_IncludesExceptionInSignal()
    {
        var sink = new SignalSink();
        using var provider = new SignalLoggerProvider(sink);
        var logger = provider.CreateLogger("Test");

        try
        {
            throw new InvalidOperationException("Test exception");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred");
        }

        Assert.True(sink.Detect(s => s.Signal.Contains("invalidoperationexception")));
    }

    [Fact]
    public void SignalLogger_TypedSinkCapturesPayload()
    {
        var untypedSink = new SignalSink();
        var typedSink = new TypedSignalSink<SignalLogPayload>(untypedSink);
        using var provider = new SignalLoggerProvider(typedSink);
        var logger = provider.CreateLogger("MyCategory");

        logger.LogWarning(new EventId(200, "LowMemory"), "Memory is low: {Percentage}%", 85);

        var payloads = typedSink.Sense();
        Assert.NotEmpty(payloads);
        Assert.Equal("MyCategory", payloads[0].Payload.Category);
        Assert.Equal(LogLevel.Warning, payloads[0].Payload.Level);
        Assert.Contains("85", payloads[0].Payload.Message);
    }

    [Fact]
    public void SignalLogHookOptions_CustomMapSignal()
    {
        var sink = new SignalSink();
        var options = new SignalLogHookOptions
        {
            MapSignal = ctx => $"custom.{ctx.Level}.{ctx.EventId.Id}"
        };
        using var provider = new SignalLoggerProvider(sink, options);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation(new EventId(42), "Test message");

        Assert.True(sink.Detect(s => s.Signal == "custom.Information.42"));
    }

    [Fact]
    public void SignalLogHookOptions_MapSignalReturnsNull_NoSignalRaised()
    {
        var sink = new SignalSink();
        var options = new SignalLogHookOptions
        {
            MapSignal = ctx => null // Suppress all signals
        };
        using var provider = new SignalLoggerProvider(sink, options);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("Should be suppressed");

        Assert.Empty(sink.Sense());
    }

    [Fact]
    public void SignalToLoggerAdapter_RaisesLogOnSignal()
    {
        var sink = new SignalSink();
        var logs = new List<(LogLevel Level, string Message)>();
        var mockLogger = new MockLogger(logs);

        var adapter = new SignalToLoggerAdapter(sink, mockLogger);

        sink.Raise("api.request.completed", "req-123");
        sink.Raise("error.timeout", "conn-456");

        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("api.request.completed"));
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("error.timeout"));
    }

    [Fact]
    public void SignalToLoggerAdapter_InfersLevelFromSignalName()
    {
        var sink = new SignalSink();
        var logs = new List<(LogLevel Level, string Message)>();
        var mockLogger = new MockLogger(logs);

        var adapter = new SignalToLoggerAdapter(sink, mockLogger);

        sink.Raise("warning.rate-limit");
        sink.Raise("debug.cache-hit");
        sink.Raise("critical.system-failure");

        Assert.Contains(logs, l => l.Level == LogLevel.Warning);
        Assert.Contains(logs, l => l.Level == LogLevel.Debug);
        Assert.Contains(logs, l => l.Level == LogLevel.Critical);
    }

    private class MockLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logs;

        public MockLogger(List<(LogLevel, string)> logs)
        {
            _logs = logs;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _logs.Add((logLevel, formatter(state, exception)));
        }
    }
}