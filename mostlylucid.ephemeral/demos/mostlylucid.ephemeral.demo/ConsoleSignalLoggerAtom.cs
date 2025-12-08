using Mostlylucid.Ephemeral;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Captures all signals in a window and outputs to console with filtering and sampling.
/// Demonstrates signal observation pattern with configurable output.
/// Supports optional ILogger integration for structured logging.
/// </summary>
public class ConsoleSignalLoggerAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly ConsoleSignalLoggerOptions _options;
    private readonly ILogger? _logger;
    private readonly List<SignalLogEntry> _logWindow = new();
    private readonly object _lock = new();
    private int _totalReceived = 0;
    private int _totalLogged = 0;
    private int _totalFiltered = 0;
    private int _sampleCounter = 0;

    public ConsoleSignalLoggerAtom(
        SignalSink sink,
        ConsoleSignalLoggerOptions? options = null,
        ILogger? logger = null)
    {
        _sink = sink;
        _options = options ?? new();
        _logger = logger;
        _sink.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent signal)
    {
        lock (_lock)
        {
            _totalReceived++;

            // Apply filters
            if (!ShouldLog(signal))
            {
                _totalFiltered++;
                return;
            }

            // Apply sampling
            _sampleCounter++;
            if (_sampleCounter % _options.SampleRate != 0)
            {
                return;
            }

            // Add to window
            var entry = new SignalLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Signal = signal.Signal,
                SequenceNumber = _totalLogged
            };

            _logWindow.Add(entry);
            _totalLogged++;

            // Trim window
            while (_logWindow.Count > _options.WindowSize)
            {
                _logWindow.RemoveAt(0);
            }

            // Output to console if enabled
            if (_options.AutoOutput)
            {
                OutputEntry(entry);
            }

            // Output to ILogger if provided
            if (_logger != null)
            {
                var logLevel = GetLogLevelForSignal(entry.Signal);
                _logger.Log(logLevel, "[{SequenceNumber}] {Signal}", entry.SequenceNumber, entry.Signal);
            }
        }
    }

    private bool ShouldLog(SignalEvent signal)
    {
        // No filters = log everything
        if (_options.IncludePatterns.Count == 0 && _options.ExcludePatterns.Count == 0)
            return true;

        // Check exclude patterns first
        foreach (var pattern in _options.ExcludePatterns)
        {
            if (StringPatternMatcher.Matches(signal.Signal, pattern))
                return false;
        }

        // If include patterns specified, must match at least one
        if (_options.IncludePatterns.Count > 0)
        {
            return _options.IncludePatterns.Any(pattern =>
                StringPatternMatcher.Matches(signal.Signal, pattern));
        }

        return true;
    }

    private void OutputEntry(SignalLogEntry entry)
    {
        var timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
        var color = GetColorForSignal(entry.Signal);

        AnsiConsole.MarkupLine($"[grey][[{timestamp}]][/] [{color}]{entry.SequenceNumber:D6}[/] {Markup.Escape(entry.Signal)}");
    }

    private string GetColorForSignal(string signal)
    {
        // Color-code by signal type
        if (signal.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            signal.Contains("critical", StringComparison.OrdinalIgnoreCase))
            return "red";
        if (signal.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            signal.Contains("warn", StringComparison.OrdinalIgnoreCase))
            return "yellow";
        if (signal.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
            return "blue";
        if (signal.StartsWith("rate.", StringComparison.OrdinalIgnoreCase))
            return "magenta";
        if (signal.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            signal.Contains("success", StringComparison.OrdinalIgnoreCase))
            return "green";
        if (signal.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
            signal.Contains("trace", StringComparison.OrdinalIgnoreCase))
            return "grey";
        return "white";
    }

    private LogLevel GetLogLevelForSignal(string signal)
    {
        // Map signal to log level using heuristics from signal name
        var first = signal.Split('.', ':')[0].ToLowerInvariant();
        return first switch
        {
            "fatal" or "critical" => LogLevel.Critical,
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            "trace" => LogLevel.Trace,
            _ => LogLevel.Information
        };
    }

    /// <summary>
    /// Dump current window contents to console
    /// </summary>
    public void DumpWindow()
    {
        lock (_lock)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Signal Log Window[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            if (_logWindow.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No signals in window[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Seq")
                .AddColumn("Timestamp")
                .AddColumn("Signal");

            foreach (var entry in _logWindow)
            {
                table.AddRow(
                    $"[grey]{entry.SequenceNumber:D6}[/]",
                    $"[grey]{entry.Timestamp:HH:mm:ss.fff}[/]",
                    Markup.Escape(entry.Signal)
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]Stats: {_totalReceived} received, {_totalLogged} logged, {_totalFiltered} filtered[/]");
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Clear the log window
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _logWindow.Clear();
            _totalReceived = 0;
            _totalLogged = 0;
            _totalFiltered = 0;
            _sampleCounter = 0;
        }
    }

    /// <summary>
    /// Get statistics about logged signals
    /// </summary>
    public ConsoleSignalLoggerStats GetStats()
    {
        lock (_lock)
        {
            return new ConsoleSignalLoggerStats
            {
                TotalReceived = _totalReceived,
                TotalLogged = _totalLogged,
                TotalFiltered = _totalFiltered,
                WindowSize = _logWindow.Count,
                MaxWindowSize = _options.WindowSize
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        return default;
    }
}

public class ConsoleSignalLoggerOptions
{
    /// <summary>
    /// Maximum number of signals to keep in window
    /// </summary>
    public int WindowSize { get; init; } = 100;

    /// <summary>
    /// Sample rate: 1 = log every signal, 2 = log every 2nd signal, etc.
    /// </summary>
    public int SampleRate { get; init; } = 1;

    /// <summary>
    /// Automatically output signals to console as they arrive
    /// </summary>
    public bool AutoOutput { get; init; } = false;

    /// <summary>
    /// Include only signals matching these patterns (empty = include all)
    /// </summary>
    public List<string> IncludePatterns { get; init; } = new();

    /// <summary>
    /// Exclude signals matching these patterns
    /// </summary>
    public List<string> ExcludePatterns { get; init; } = new();
}

public record SignalLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Signal { get; init; } = "";
    public int SequenceNumber { get; init; }
}

public record ConsoleSignalLoggerStats
{
    public int TotalReceived { get; init; }
    public int TotalLogged { get; init; }
    public int TotalFiltered { get; init; }
    public int WindowSize { get; init; }
    public int MaxWindowSize { get; init; }
}
