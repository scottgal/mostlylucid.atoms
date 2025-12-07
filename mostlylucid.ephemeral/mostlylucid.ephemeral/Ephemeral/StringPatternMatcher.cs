using System.Runtime.CompilerServices;

namespace Mostlylucid.Helpers.Ephemeral;

/// <summary>
/// Minimal glob-style matcher for signal/key filters. Supports '*' and '?' wildcards and comma-separated patterns.
/// </summary>
internal static class StringPatternMatcher
{
    public static bool MatchesAny(string value, IReadOnlySet<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
            return false;

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (MatchesCommaSeparated(value, raw.AsSpan()))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches(string value, string pattern)
        => Matches(value.AsSpan(), pattern.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesCommaSeparated(string value, ReadOnlySpan<char> patterns)
    {
        var span = patterns;
        while (!span.IsEmpty)
        {
            var idx = span.IndexOf(',');
            ReadOnlySpan<char> token;
            if (idx == -1)
            {
                token = span.Trim();
                span = ReadOnlySpan<char>.Empty;
            }
            else
            {
                token = span[..idx].Trim();
                span = span[(idx + 1)..];
            }

            if (!token.IsEmpty && Matches(value.AsSpan(), token))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches(ReadOnlySpan<char> value, ReadOnlySpan<char> pattern)
    {
        // Fast path exact match when no wildcards present
        if (pattern.IndexOfAny(['*', '?']) == -1)
            return value.SequenceEqual(pattern);

        // Glob match with '*' and '?'
        var t = 0;
        var p = 0;
        var star = -1;
        var match = 0;

        while (t < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[t]))
            {
                t++;
                p++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                match = t;
                continue;
            }

            if (star != -1)
            {
                p = star + 1;
                t = ++match;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
