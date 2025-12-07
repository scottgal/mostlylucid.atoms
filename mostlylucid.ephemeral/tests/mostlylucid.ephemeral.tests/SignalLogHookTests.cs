using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Logging;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class SignalLogHookTests
{
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, EventId Id, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => DummyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, formatter(state, exception)));
    }

    private sealed class DummyScope : IDisposable
    {
        public static readonly DummyScope Instance = new();
        public void Dispose() { }
    }

    [Fact]
    public void Logs_Map_To_Signals()
    {
        var sink = new TypedSignalSink<SignalLogPayload>();
        var provider = new SignalLoggerProvider(sink, new SignalLogHookOptions
        {
            MinimumLevel = LogLevel.Warning
        });

        var logger = provider.CreateLogger("OrderService");
        logger.LogWarning(new EventId(42, "orders.fetch.failed"), "Failed to fetch order {OrderId}", 123);

        var signals = sink.Untyped.Sense();
        Assert.Single(signals);
        Assert.StartsWith("log.warning.orderservice.orders-fetch-failed", signals[0].Signal);
    }

    [Fact]
    public void Payload_Includes_Exception_Metadata()
    {
        var sink = new TypedSignalSink<SignalLogPayload>();
        var provider = new SignalLoggerProvider(sink, new SignalLogHookOptions
        {
            MinimumLevel = LogLevel.Error
        });

        var logger = provider.CreateLogger("OrderService");
        logger.LogError(new EventId(7, "orders.db"), new InvalidOperationException("boom"), "DB failure");

        var payload = sink.Sense().Single();
        Assert.Equal("DB failure", payload.Payload.Message);
        Assert.Equal("System.InvalidOperationException", payload.Payload.ExceptionType);
        Assert.Equal(LogLevel.Error, payload.Payload.Level);
    }

    [Fact]
    public void Signals_Can_Map_To_Logs()
    {
        var sink = new SignalSink();
        var logger = new CapturingLogger();
        using var bridge = new SignalToLoggerAdapter(sink, logger);

        sink.Raise(new SignalEvent("error.db.timeout", 123, "order-42", DateTimeOffset.UtcNow));

        Assert.Single(logger.Entries);
        var entry = logger.Entries[0];
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("error.db.timeout", entry.Message);
        Assert.Equal("order-42", entry.Id.Name);
    }
}
