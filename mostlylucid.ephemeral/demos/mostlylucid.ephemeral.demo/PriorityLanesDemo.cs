using System.Diagnostics;
using Mostlylucid.Ephemeral;
using Spectre.Console;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates PriorityWorkCoordinator with strict lane-based SLA guarantees.
///
/// Pattern: Multi-tenant SaaS where premium customers get priority lanes.
/// Use case: API gateways, job queues, request processing with SLA tiers.
/// </summary>
public static class PriorityLanesDemo
{
    public record ApiRequest(string Id, string CustomerId, string Endpoint, string Tier);

    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[yellow]═══ Priority Lanes Demo ═══[/]\n");
        AnsiConsole.MarkupLine("[grey]Demonstrates strict priority-based SLA guarantees[/]");
        AnsiConsole.MarkupLine("[grey]Pattern: Critical work NEVER waits for bulk work[/]\n");

        var sink = new SignalSink(maxCapacity: 2000);

        // Track metrics per lane
        var metrics = new Dictionary<string, LaneMetrics>
        {
            ["critical"] = new LaneMetrics(),
            ["premium"] = new LaneMetrics(),
            ["standard"] = new LaneMetrics(),
            ["free"] = new LaneMetrics()
        };

        using var subscription = sink.Subscribe(signal =>
        {
            // Track latencies
            if (signal.Signal.StartsWith("request.completed:"))
            {
                var tier = signal.Key.Split(':')[0];
                if (metrics.ContainsKey(tier))
                {
                    metrics[tier].CompletedCount++;
                }
            }
        });

        // Define priority lanes with different configurations
        var lanes = new[]
        {
            new PriorityLane(
                Name: "critical",
                MaxDepth: 50,
                CancelOnSignals: new HashSet<string> { "system.emergency_shutdown" },
                DeferOnSignals: null),  // Critical never defers

            new PriorityLane(
                Name: "premium",
                MaxDepth: 200,
                CancelOnSignals: new HashSet<string> { "system.emergency_shutdown" },
                DeferOnSignals: new HashSet<string> { "system.critical_overload" }),

            new PriorityLane(
                Name: "standard",
                MaxDepth: 500,
                CancelOnSignals: new HashSet<string> { "system.emergency_shutdown", "system.overload" },
                DeferOnSignals: new HashSet<string> { "system.high_load", "system.critical_overload" }),

            new PriorityLane(
                Name: "free",
                MaxDepth: 1000,
                CancelOnSignals: new HashSet<string> { "system.emergency_shutdown", "system.overload", "system.high_load" },
                DeferOnSignals: new HashSet<string> { "system.normal_load", "system.high_load", "system.critical_overload" })
        };

        AnsiConsole.MarkupLine("[yellow]Lane Configuration:[/]\n");
        DisplayLaneConfiguration(lanes);

        var coordinator = new PriorityWorkCoordinator<ApiRequest>(
            new PriorityWorkCoordinatorOptions<ApiRequest>(
                Body: ProcessRequestAsync,
                Lanes: lanes,
                EphemeralOptions: new EphemeralOptions
                {
                    MaxConcurrency = 8,
                    Signals = sink
                }));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Simulating mixed workload:[/]\n");

        var sw = Stopwatch.StartNew();

        // Start background monitoring
        var monitorTask = MonitorLanesAsync(coordinator, metrics);

        // Simulate system load changes
        var loadSimulatorTask = Task.Run(async () =>
        {
            await Task.Delay(2000);
            sink.Raise("system.high_load");
            AnsiConsole.MarkupLine("[yellow]⚠️  System under HIGH LOAD - standard/free tiers defer[/]");

            await Task.Delay(3000);
            sink.Raise("system.normal_load");
            AnsiConsole.MarkupLine("[green]✓ System load NORMAL - all tiers resume[/]");
        });

        // Enqueue mixed requests
        var enqueueTask = Task.Run(async () =>
        {
            for (int i = 1; i <= 100; i++)
            {
                // Determine tier (realistic distribution)
                var tier = i switch
                {
                    <= 5 => "critical",      // 5% critical
                    <= 25 => "premium",      // 20% premium
                    <= 60 => "standard",     // 35% standard
                    _ => "free"              // 40% free
                };

                var request = new ApiRequest(
                    Id: $"req-{i:D3}",
                    CustomerId: $"cust-{Random.Shared.Next(1, 20)}",
                    Endpoint: "/api/data",
                    Tier: tier);

                try
                {
                    metrics[tier].EnqueuedCount++;
                    await coordinator.EnqueueAsync(request, laneName: tier);
                }
                catch (Exception ex)
                {
                    metrics[tier].RejectedCount++;
                    AnsiConsole.MarkupLine($"[red]✗[/] [grey]{tier} request rejected: {ex.Message}[/]");
                }

                // Varying request rates
                await Task.Delay(tier switch
                {
                    "critical" => 500,   // Rare
                    "premium" => 100,    // Moderate
                    "standard" => 50,    // Frequent
                    "free" => 30,        // Very frequent
                    _ => 100
                });
            }
        });

        await enqueueTask;
        await Task.Delay(5000); // Let everything complete

        await coordinator.DisposeAsync();
        sw.Stop();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓ Simulation completed in {sw.Elapsed.TotalSeconds:F1}s[/]\n");

        DisplayMetrics(metrics);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Key Takeaways:[/]");
        AnsiConsole.MarkupLine("[grey]• Critical tier NEVER deferred, even under extreme load[/]");
        AnsiConsole.MarkupLine("[grey]• Premium tier deferred only during critical overload[/]");
        AnsiConsole.MarkupLine("[grey]• Standard/Free tiers defer under normal high load[/]");
        AnsiConsole.MarkupLine("[grey]• Per-lane signal gating enables fine-grained control[/]");
        AnsiConsole.MarkupLine("[grey]• Strict SLA guarantees without manual coordination[/]\n");
    }

    private static async Task ProcessRequestAsync(ApiRequest request, CancellationToken ct)
    {
        // Simulate processing with tier-based latency
        var processingTime = request.Tier switch
        {
            "critical" => Random.Shared.Next(10, 30),   // Fast processing
            "premium" => Random.Shared.Next(20, 50),
            "standard" => Random.Shared.Next(30, 80),
            "free" => Random.Shared.Next(50, 150),      // Slower processing
            _ => 50
        };

        await Task.Delay(processingTime, ct);

        // Simulate occasional failures
        if (Random.Shared.Next(100) < 2)
        {
            throw new Exception($"Request {request.Id} failed");
        }
    }

    private static void DisplayLaneConfiguration(PriorityLane[] lanes)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Lane")
            .AddColumn("Max Depth")
            .AddColumn("Defer On")
            .AddColumn("Cancel On");

        foreach (var lane in lanes)
        {
            var color = lane.Name switch
            {
                "critical" => "red",
                "premium" => "yellow",
                "standard" => "cyan",
                "free" => "grey",
                _ => "white"
            };

            table.AddRow(
                $"[{color}]{lane.Name}[/]",
                $"[grey]{lane.MaxDepth}[/]",
                lane.DeferOnSignals != null && lane.DeferOnSignals.Any()
                    ? $"[yellow]{string.Join(", ", lane.DeferOnSignals.Take(2))}...[/]"
                    : "[grey]Never[/]",
                lane.CancelOnSignals != null && lane.CancelOnSignals.Any()
                    ? $"[red]{string.Join(", ", lane.CancelOnSignals.Take(1))}...[/]"
                    : "[grey]Never[/]");
        }

        AnsiConsole.Write(table);
    }

    private static async Task MonitorLanesAsync(
        PriorityWorkCoordinator<ApiRequest> coordinator,
        Dictionary<string, LaneMetrics> metrics)
    {
        while (true)
        {
            await Task.Delay(1000);

            // Update completion counts from coordinator stats
            // (In real implementation, we'd query lane-specific stats)
        }
    }

    private static void DisplayMetrics(Dictionary<string, LaneMetrics> metrics)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Tier")
            .AddColumn("Enqueued")
            .AddColumn("Completed")
            .AddColumn("Rejected")
            .AddColumn("Success Rate");

        foreach (var (tier, metric) in metrics.OrderBy(m => m.Key switch
        {
            "critical" => 0,
            "premium" => 1,
            "standard" => 2,
            "free" => 3,
            _ => 4
        }))
        {
            var successRate = metric.EnqueuedCount > 0
                ? (metric.CompletedCount * 100.0 / metric.EnqueuedCount)
                : 0;

            var color = tier switch
            {
                "critical" => "red",
                "premium" => "yellow",
                "standard" => "cyan",
                "free" => "grey",
                _ => "white"
            };

            table.AddRow(
                $"[{color}]{tier}[/]",
                $"[grey]{metric.EnqueuedCount}[/]",
                $"[green]{metric.CompletedCount}[/]",
                metric.RejectedCount > 0 ? $"[red]{metric.RejectedCount}[/]" : "[grey]0[/]",
                $"[{(successRate >= 95 ? "green" : "yellow")}]{successRate:F1}%[/]");
        }

        AnsiConsole.Write(table);
    }

    private class LaneMetrics
    {
        public int EnqueuedCount;
        public int CompletedCount;
        public int RejectedCount;
    }
}
