using System.Diagnostics;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Signals;
using Spectre.Console;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates DecayingReputationWindow for time-based forgiveness.
///
/// Pattern: Exponential decay makes penalties age out naturally.
/// Use case: IP reputation, rate limiting, user behavior scoring, anomaly detection.
/// </summary>
public static class DecayingReputationDemo
{
    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[yellow]═══ Decaying Reputation Demo ═══[/]\n");
        AnsiConsole.MarkupLine("[grey]Demonstrates time-based forgiveness with exponential decay[/]");
        AnsiConsole.MarkupLine("[grey]Pattern: Bad behavior is penalized, but forgiven over time[/]\n");

        var sink = new SignalSink(maxCapacity: 1000);

        // Track signal events
        using var subscription = sink.Subscribe(signal =>
        {
            var color = signal.Signal switch
            {
                var s when s.Contains("penalty") => "red",
                var s when s.Contains("reward") => "green",
                var s when s.Contains("threshold") => "yellow",
                _ => "grey"
            };

            if (!signal.Signal.StartsWith("reputation.score"))
            {
                AnsiConsole.MarkupLine($"[{color}]{signal.Signal}[/] [grey]({signal.Key})[/]");
            }
        });

        // Create reputation tracker with 30-second half-life
        var reputation = new DecayingReputationWindow<string>(
            halfLife: TimeSpan.FromSeconds(30),  // Score decays by 50% every 30 seconds
            maxSize: 100,
            signals: sink);

        AnsiConsole.MarkupLine("[yellow]Scenario: IP Address Reputation Tracking[/]\n");
        AnsiConsole.MarkupLine("[grey]Half-life: 30 seconds (score × 0.5 every 30s)[/]");
        AnsiConsole.MarkupLine("[grey]Thresholds: < -5.0 = blocked, > 5.0 = trusted[/]\n");

        // Simulate IP behavior over time
        var scenarios = new[]
        {
            ("Good IP (192.168.1.100)", "ip-good", new[] { 1.0, 1.0, 1.0, 1.0, 1.0 }),
            ("Suspicious IP (192.168.1.101)", "ip-suspicious", new[] { -2.0, -2.0, -1.0, 1.0, 1.0 }),
            ("Bad IP (192.168.1.102)", "ip-bad", new[] { -3.0, -3.0, -3.0, -2.0, -1.0 }),
            ("Recovering IP (192.168.1.103)", "ip-recovering", new[] { -5.0, -3.0, 0.0, 0.0, 0.0 })
        };

        var sw = Stopwatch.StartNew();

        foreach (var (name, ip, scores) in scenarios)
        {
            AnsiConsole.MarkupLine($"[cyan]═══ {name} ═══[/]\n");

            for (int i = 0; i < scores.Length; i++)
            {
                var score = scores[i];
                reputation.Update(ip, score);

                var currentScore = reputation.GetScore(ip);
                var status = GetReputationStatus(currentScore);

                AnsiConsole.MarkupLine(
                    $"[grey]t={sw.Elapsed.TotalSeconds:F1}s:[/] " +
                    $"Event score: {FormatScore(score)} | " +
                    $"Total: {FormatScore(currentScore)} | " +
                    $"Status: {status}");

                // Wait a bit between events
                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            AnsiConsole.WriteLine();
        }

        // Show decay in action
        AnsiConsole.MarkupLine("[yellow]═══ Observing Natural Decay ═══[/]\n");
        AnsiConsole.MarkupLine("[grey]Watching bad IP reputation decay over time (no new events)...[/]\n");

        var badIp = "ip-bad";
        var initialScore = reputation.GetScore(badIp);

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));

            var currentScore = reputation.GetScore(badIp);
            var decayPercent = initialScore != 0
                ? ((currentScore - initialScore) / initialScore) * 100
                : 0;

            AnsiConsole.MarkupLine(
                $"[grey]t={sw.Elapsed.TotalSeconds:F1}s:[/] " +
                $"Score: {FormatScore(currentScore)} " +
                $"[grey](decayed {Math.Abs(decayPercent):F1}%)[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]═══ Final Reputation Scores ═══[/]\n");

        DisplayFinalScores(reputation, scenarios.Select(s => (s.Item1, s.Item2)).ToArray());

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Key Takeaways:[/]");
        AnsiConsole.MarkupLine("[grey]• Exponential decay: score(t) = initial × e^(-λt)[/]");
        AnsiConsole.MarkupLine("[grey]• Bad behavior is penalized immediately[/]");
        AnsiConsole.MarkupLine("[grey]• Penalties naturally fade if behavior improves[/]");
        AnsiConsole.MarkupLine("[grey]• No manual cleanup needed - decay is automatic[/]");
        AnsiConsole.MarkupLine("[grey]• Perfect for adaptive systems that \"forgive\" old mistakes[/]\n");

        // Demonstrate use case: Rate limiting based on reputation
        AnsiConsole.MarkupLine("[yellow]═══ Use Case: Adaptive Rate Limiting ═══[/]\n");
        DemonstrateAdaptiveRateLimiting(reputation);
    }

    private static string GetReputationStatus(double score)
    {
        return score switch
        {
            < -5.0 => "[red]BLOCKED[/]",
            < -2.0 => "[yellow]SUSPICIOUS[/]",
            < 2.0 => "[grey]NEUTRAL[/]",
            < 5.0 => "[cyan]GOOD[/]",
            _ => "[green]TRUSTED[/]"
        };
    }

    private static string FormatScore(double score)
    {
        var color = score switch
        {
            < -2.0 => "red",
            < 0 => "yellow",
            < 2.0 => "grey",
            _ => "green"
        };

        var prefix = score > 0 ? "+" : "";
        return $"[{color}]{prefix}{score:F2}[/]";
    }

    private static void DisplayFinalScores(
        DecayingReputationWindow<string> reputation,
        (string Name, string Ip)[] ips)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("IP Address")
            .AddColumn("Current Score")
            .AddColumn("Status")
            .AddColumn("Action");

        foreach (var (name, ip) in ips)
        {
            var score = reputation.GetScore(ip);
            var status = GetReputationStatus(score);

            var action = score switch
            {
                < -5.0 => "[red]Block all requests[/]",
                < -2.0 => "[yellow]Apply strict rate limit[/]",
                < 2.0 => "[grey]Normal rate limit[/]",
                < 5.0 => "[cyan]Relaxed rate limit[/]",
                _ => "[green]Bypass rate limit[/]"
            };

            table.AddRow(
                $"[cyan]{name}[/]",
                FormatScore(score),
                status,
                action);
        }

        AnsiConsole.Write(table);
    }

    private static void DemonstrateAdaptiveRateLimiting(
        DecayingReputationWindow<string> reputation)
    {
        AnsiConsole.MarkupLine("[grey]Adaptive rate limits based on reputation:[/]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Reputation Score")
            .AddColumn("Requests/Minute")
            .AddColumn("Burst")
            .AddColumn("Rationale");

        table.AddRow(
            "[green]> 5.0 (Trusted)[/]",
            "[green]∞ (unlimited)[/]",
            "[green]∞[/]",
            "Proven good actor");

        table.AddRow(
            "[cyan]2.0 - 5.0 (Good)[/]",
            "[cyan]1000/min[/]",
            "[cyan]100[/]",
            "Generous limits");

        table.AddRow(
            "[grey]-2.0 - 2.0 (Neutral)[/]",
            "[grey]100/min[/]",
            "[grey]20[/]",
            "Standard limits");

        table.AddRow(
            "[yellow]-5.0 - -2.0 (Suspicious)[/]",
            "[yellow]10/min[/]",
            "[yellow]3[/]",
            "Strict limits");

        table.AddRow(
            "[red]< -5.0 (Blocked)[/]",
            "[red]0 (blocked)[/]",
            "[red]0[/]",
            "Completely blocked");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Benefits:[/]");
        AnsiConsole.MarkupLine("[grey]• Good actors get better service over time[/]");
        AnsiConsole.MarkupLine("[grey]• Bad actors are penalized but can recover[/]");
        AnsiConsole.MarkupLine("[grey]• One-time mistakes don't permanently hurt reputation[/]");
        AnsiConsole.MarkupLine("[grey]• System adapts to behavior patterns automatically[/]\n");
    }
}
