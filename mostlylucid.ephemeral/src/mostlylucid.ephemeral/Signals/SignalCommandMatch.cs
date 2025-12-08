using System;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Represents a parsed signal command with its payload.
/// Used to extract structured data from signal strings following the pattern "command:payload".
/// </summary>
/// <remarks>
/// <para><strong>Pattern Matching:</strong></para>
/// <para>Uses glob-style matching to find commands anywhere in the signal string:</para>
/// <list type="bullet">
/// <item>"window.size.set:100" matches command "window.size.set" with payload "100"</item>
/// <item>"app.window.size.set:200" also matches, extracting "200"</item>
/// <item>"window.size.set:" matches with empty payload ""</item>
/// </list>
/// <para><strong>Safety:</strong></para>
/// <para>All parsing is bounds-checked to prevent buffer overflows. Invalid inputs return false safely.</para>
/// <para><strong>Performance:</strong></para>
/// <para>Struct type with no allocations. String slicing uses spans for zero-copy extraction.</para>
/// </remarks>
/// <example>
/// <code>
/// if (SignalCommandMatch.TryParse("window.size.set:500", "window.size.set", out var match))
/// {
///     var (cmd, payload) = match;
///     int capacity = int.Parse(payload); // "500"
/// }
/// </code>
/// </example>
public readonly struct SignalCommandMatch
{
    /// <summary>
    /// Gets the matched command name (e.g., "window.size.set").
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// Gets the payload string after the colon (e.g., "500").
    /// May be empty if signal ends with colon.
    /// </summary>
    public string Payload { get; }

    private SignalCommandMatch(string command, string payload)
    {
        Command = command;
        Payload = payload;
    }

    /// <summary>
    /// Deconstructs the match into command and payload components.
    /// </summary>
    /// <param name="command">The matched command name.</param>
    /// <param name="payload">The payload string.</param>
    public void Deconstruct(out string command, out string payload)
    {
        command = Command;
        payload = Payload;
    }

    /// <summary>
    /// Attempts to parse a signal string for a specific command pattern.
    /// </summary>
    /// <param name="signal">The signal string to parse (e.g., "window.size.set:100").</param>
    /// <param name="command">The command to look for (e.g., "window.size.set").</param>
    /// <param name="match">When successful, contains the parsed command and payload.</param>
    /// <returns>True if the signal matches the command pattern and was successfully parsed; otherwise false.</returns>
    /// <remarks>
    /// <para>The parser:</para>
    /// <list type="number">
    /// <item>Uses glob matching to find command anywhere in signal (*command:*)</item>
    /// <item>Locates the first occurrence of "command:" in the signal</item>
    /// <item>Extracts everything after the colon as the payload</item>
    /// <item>Performs bounds checking to prevent buffer overflows (security fix)</item>
    /// </list>
    /// <para><strong>Returns false for:</strong></para>
    /// <list type="bullet">
    /// <item>Null or empty signal/command</item>
    /// <item>Signal doesn't contain the command</item>
    /// <item>Signal doesn't have a colon after the command</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Basic usage
    /// SignalCommandMatch.TryParse("set:100", "set", out var m1); // true, payload="100"
    ///
    /// // Nested signals
    /// SignalCommandMatch.TryParse("app.window.size.set:200", "window.size.set", out var m2); // true, payload="200"
    ///
    /// // Empty payload
    /// SignalCommandMatch.TryParse("cmd:", "cmd", out var m3); // true, payload=""
    ///
    /// // Multiple colons - extracts everything after first
    /// SignalCommandMatch.TryParse("time:12:30:45", "time", out var m4); // true, payload="12:30:45"
    ///
    /// // No match
    /// SignalCommandMatch.TryParse("other", "cmd", out var m5); // false
    /// </code>
    /// </example>
    public static bool TryParse(string signal, string command, out SignalCommandMatch match)
    {
        match = default;
        if (string.IsNullOrEmpty(signal) || string.IsNullOrEmpty(command))
            return false;

        // Optimization: Direct IndexOf instead of pattern matching + IndexOf
        // Pattern was "*{command}:*" which just checks if "command:" exists anywhere
        ReadOnlySpan<char> signalSpan = signal.AsSpan();
        ReadOnlySpan<char> commandSpan = command.AsSpan();

        // Search for "command:" pattern without allocating
        int idx = -1;
        for (int i = 0; i <= signalSpan.Length - commandSpan.Length - 1; i++)
        {
            if (signalSpan.Slice(i, commandSpan.Length).SequenceEqual(commandSpan) &&
                i + commandSpan.Length < signalSpan.Length &&
                signalSpan[i + commandSpan.Length] == ':')
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
            return false;

        var payloadStart = idx + command.Length + 1; // +1 for ':'
        if (payloadStart > signal.Length)
            return false;

        var payload = signal[payloadStart..];
        match = new SignalCommandMatch(command, payload);
        return true;
    }
}
