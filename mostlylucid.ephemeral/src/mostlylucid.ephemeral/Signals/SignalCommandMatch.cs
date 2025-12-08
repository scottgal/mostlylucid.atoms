using System;

namespace Mostlylucid.Ephemeral;

public readonly struct SignalCommandMatch
{
    public string Command { get; }
    public string Payload { get; }

    private SignalCommandMatch(string command, string payload)
    {
        Command = command;
        Payload = payload;
    }

    public void Deconstruct(out string command, out string payload)
    {
        command = Command;
        payload = Payload;
    }

    public static bool TryParse(string signal, string command, out SignalCommandMatch match)
    {
        match = default;
        if (string.IsNullOrEmpty(signal) || string.IsNullOrEmpty(command))
            return false;

        var pattern = $"*{command}:*";
        if (!StringPatternMatcher.Matches(signal, pattern))
            return false;

        var prefix = command + ":";
        var idx = signal.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var payload = signal[(idx + prefix.Length)..];
        match = new SignalCommandMatch(command, payload);
        return true;
    }
}
