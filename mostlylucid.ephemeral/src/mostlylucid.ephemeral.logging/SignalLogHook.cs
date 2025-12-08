using System.Text;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Ephemeral.Logging;

/// <summary>
///     Hooks ILogger messages into SignalSink as typed signals.
/// </summary>
public sealed class SignalLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly SignalLogHookOptions _options;
    private readonly TypedSignalSink<SignalLogPayload> _typedSink;
    private IExternalScopeProvider? _scopeProvider;

    /// <summary>
    ///     Create a provider with an existing typed sink (preferred when you want payloads).
    /// </summary>
    public SignalLoggerProvider(TypedSignalSink<SignalLogPayload> sink, SignalLogHookOptions? options = null)
    {
        _typedSink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new SignalLogHookOptions();
    }

    /// <summary>
    ///     Convenience overload that wraps an untyped sink so payloads are still captured.
    /// </summary>
    public SignalLoggerProvider(SignalSink sink, SignalLogHookOptions? options = null)
        : this(new TypedSignalSink<SignalLogPayload>(sink), options)
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SignalLogger(categoryName, _typedSink, _options, () => _scopeProvider);
    }

    public void Dispose()
    {
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }
}

public sealed class SignalLogger : ILogger
{
    private readonly string _category;
    private readonly SignalLogHookOptions _options;
    private readonly Func<IExternalScopeProvider?> _scopeProviderFactory;
    private readonly SignalSink _sink;
    private readonly TypedSignalSink<SignalLogPayload> _typedSink;

    public SignalLogger(string category, TypedSignalSink<SignalLogPayload> sink, SignalLogHookOptions options,
        Func<IExternalScopeProvider?> scopeProviderFactory)
    {
        _category = category;
        _typedSink = sink;
        _sink = sink.Untyped;
        _options = options;
        _scopeProviderFactory = scopeProviderFactory;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _scopeProviderFactory()?.Push(state) ?? NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _options.MinimumLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var scopeProvider = _scopeProviderFactory();
        var message = formatter(state, exception);

        Dictionary<string, string?>? scopeProps = null;
        scopeProvider?.ForEachScope((scope, _) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                scopeProps ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var kvp in kvps)
                    scopeProps[kvp.Key] = kvp.Value?.ToString();
            }
        }, state);

        var ctx = new SignalLogContext(
            _category,
            logLevel,
            eventId,
            message,
            exception,
            state,
            scopeProps);

        var signal = _options.MapSignal(ctx);
        if (signal is null)
            return;

        var payload = _options.MapPayload?.Invoke(ctx);
        if (payload is null)
            _sink.Raise(new SignalEvent(signal, EphemeralIdGenerator.NextId(),
                ctx.EventId.Name ?? ctx.EventId.Id.ToString(), DateTimeOffset.UtcNow));
        else
            _typedSink.Raise(new SignalEvent<SignalLogPayload>(
                signal,
                EphemeralIdGenerator.NextId(),
                ctx.EventId.Name ?? ctx.EventId.Id.ToString(),
                DateTimeOffset.UtcNow,
                payload.Value));
    }
}

/// <summary>
///     Options to map logs to signals.
/// </summary>
public sealed class SignalLogHookOptions
{
    /// <summary>Minimum level to emit signals.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Map log context to a signal name (null to ignore).</summary>
    public Func<SignalLogContext, string?> MapSignal { get; set; } = DefaultMap;

    /// <summary>Map log context to payload (optional).</summary>
    public Func<SignalLogContext, SignalLogPayload?>? MapPayload { get; set; } = DefaultPayload;

    private static string? DefaultMap(SignalLogContext ctx)
    {
        // log.{level}.{category}.{eventName|id}[:exception]
        var level = ctx.Level.ToString().ToLowerInvariant();
        var category = Slugify(ctx.Category);
        var rawEvent = !string.IsNullOrWhiteSpace(ctx.EventId.Name)
            ? ctx.EventId.Name!
            : ctx.EventId.Id != 0
                ? ctx.EventId.Id.ToString()
                : "none";
        var eventPart = Slugify(rawEvent);
        var exPart = ctx.Exception is null ? string.Empty : $":{Slugify(ctx.Exception.GetType().Name)}";
        return $"log.{level}.{category}.{eventPart}{exPart}";
    }

    private static SignalLogPayload? DefaultPayload(SignalLogContext ctx)
    {
        var ex = ctx.Exception;
        return new SignalLogPayload(
            ctx.Message,
            ctx.EventId,
            ctx.Category,
            ctx.Level,
            ex?.GetType().FullName,
            ex?.Message,
            ex?.HResult,
            ctx.ScopeProperties);
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length == 0 || builder[^1] != '-') builder.Append('-');

        // trim trailing delimiter if needed
        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;

        return builder.Length == 0 ? "none" : builder.ToString();
    }
}

public readonly record struct SignalLogContext(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    object? State,
    IReadOnlyDictionary<string, string?>? ScopeProperties);

public readonly record struct SignalLogPayload(
    string Message,
    EventId EventId,
    string Category,
    LogLevel Level,
    string? ExceptionType,
    string? ExceptionMessage,
    int? ExceptionHResult,
    IReadOnlyDictionary<string, string?>? ScopeProperties);

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}