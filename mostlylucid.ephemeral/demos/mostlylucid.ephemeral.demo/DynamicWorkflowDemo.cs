using Mostlylucid.Ephemeral;
using Spectre.Console;
using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates the Dynamic Adaptive Workflow pattern using PROPER Ephemeral coordinators:
/// - Priority-based failover (primary → backup processor)
/// - Dynamic routing based on health signals
/// - Adaptive concurrency adjustment
/// - Self-healing via periodic probing
/// </summary>
public static class DynamicWorkflowDemo
{
    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[yellow]Dynamic Adaptive Workflow Pattern[/]");
        AnsiConsole.MarkupLine("[grey]Priority failover + adaptive concurrency + self-healing[/]\n");
        AnsiConsole.MarkupLine("[cyan]⚡ Using PROPER coordinators (no event handlers!)[/]\n");

        var globalSink = new SignalSink(maxCapacity: 5000);

        // Track routing decisions and metrics
        var routingTable = new ConcurrentDictionary<string, int>(); // Entity → Priority
        var processorHealth = new ConcurrentDictionary<int, bool> { [1] = true, [2] = true };
        var metrics = new WorkflowMetrics();

        // Subscribe for visualization only
        var visualizationSub = globalSink.Subscribe(signal =>
        {
            var color = signal.Signal switch
            {
                var s when s.Contains("route.assigned") => "cyan",
                var s when s.Contains("processing.complete") => "green",
                var s when s.Contains("processing.failed") => "red",
                var s when s.Contains("failover") => "yellow",
                var s when s.Contains("unhealthy") => "red",
                var s when s.Contains("probe.success") => "green",
                var s when s.Contains("concurrency") => "blue",
                _ => "grey"
            };

            if (!signal.Signal.Contains("health.good")) // Reduce noise
            {
                AnsiConsole.MarkupLine($"[{color}]{signal.Signal.Substring(0, Math.Min(60, signal.Signal.Length))}[/]");
            }

            // Track metrics
            if (signal.Signal.Contains("processing.complete")) metrics.Successful++;
            if (signal.Signal.Contains("processing.failed")) metrics.Failed++;
            if (signal.Signal.Contains("failover")) metrics.Failovers++;
        });

        AnsiConsole.MarkupLine("[cyan]Setting up workflow system with coordinators...[/]\n");

        // Simulated processor failure rates
        var processorAFailureRate = 0.3; // 30% failure rate (will trigger failover)
        var processorBFailureRate = 0.05; // 5% failure rate (reliable backup)

        // Priority 1 Coordinator (Primary Processor)
        await using var processor1 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                globalSink.Raise($"processing.started:pri1:{widgetId}");
                await Task.Delay(Random.Shared.Next(30, 80), ct);
                var success = Random.Shared.NextDouble() > processorAFailureRate;

                if (success)
                {
                    processorHealth[1] = true;
                    globalSink.Raise($"processing.complete:pri1:{widgetId}");
                    globalSink.Raise("processor.pri1.health.good");
                }
                else
                {
                    globalSink.Raise($"processing.failed:pri1:{widgetId}");

                    // Check if we should mark unhealthy (OPTIMIZED: use CountRecentByPrefix)
                    var recentFailures = globalSink.CountRecentByPrefix(
                        "processing.failed:pri1",
                        DateTimeOffset.UtcNow.AddSeconds(-10));

                    if (recentFailures >= 3 && processorHealth[1])
                    {
                        processorHealth[1] = false;
                        globalSink.Raise($"processor.pri1.unhealthy:failures={recentFailures}");
                        globalSink.Raise($"failover.triggered:pri1→pri2");
                    }
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 4,
                Signals = globalSink
            });

        // Priority 2 Coordinator (Backup Processor)
        await using var processor2 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                globalSink.Raise($"processing.started:pri2:{widgetId}");
                await Task.Delay(Random.Shared.Next(50, 120), ct);
                var success = Random.Shared.NextDouble() > processorBFailureRate;

                if (success)
                {
                    processorHealth[2] = true;
                    globalSink.Raise($"processing.complete:pri2:{widgetId}");
                    globalSink.Raise("processor.pri2.health.good");
                }
                else
                {
                    globalSink.Raise($"processing.failed:pri2:{widgetId}");
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 4,
                Signals = globalSink
            });

        // Router coordinator - decides which processor gets the work
        await using var router = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                var targetPriority = processorHealth[1] ? 1 : 2;
                routingTable[widgetId] = targetPriority;

                globalSink.Raise($"route.assigned:pri{targetPriority}:{widgetId}");

                // Enqueue to appropriate processor
                if (targetPriority == 1)
                {
                    await processor1.EnqueueAsync(widgetId);
                }
                else
                {
                    await processor2.EnqueueAsync(widgetId);
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 16, // Router is fast
                Signals = globalSink
            });

        // Prober coordinator - tests unhealthy processors for recovery
        var proberCts = new CancellationTokenSource();
        var probeTask = Task.Run(async () =>
        {
            while (!proberCts.Token.IsCancellationRequested && metrics.Total < 50)
            {
                await Task.Delay(3000, proberCts.Token);

                if (!processorHealth[1])
                {
                    globalSink.Raise("probe.testing:pri1");

                    // Simulate probe
                    var recovered = Random.Shared.NextDouble() > 0.3;
                    if (recovered)
                    {
                        globalSink.Raise("probe.success:pri1");
                        processorHealth[1] = true;
                        AnsiConsole.MarkupLine("[green]✓ Processor 1 recovered! Restoring primary routing[/]");
                    }
                    else
                    {
                        globalSink.Raise("probe.failed:pri1");
                    }
                }
            }
        }, proberCts.Token);

        // Concurrency adjuster - monitors and adjusts concurrency
        var concurrencyCts = new CancellationTokenSource();
        var currentConcurrency = 4;
        var concurrencyTask = Task.Run(async () =>
        {
            while (!concurrencyCts.Token.IsCancellationRequested && metrics.Total < 50)
            {
                await Task.Delay(2000, concurrencyCts.Token);

                var total = metrics.Successful + metrics.Failed;
                var failureRate = total > 0 ? (double)metrics.Failed / total : 0;

                if (failureRate > 0.2 && currentConcurrency > 2)
                {
                    currentConcurrency -= 2;
                    processor1.SetMaxConcurrency(currentConcurrency);
                    processor2.SetMaxConcurrency(currentConcurrency);
                    globalSink.Raise($"concurrency.decreased:to={currentConcurrency}:reason=high-failure-rate");
                }
                else if (failureRate < 0.1 && currentConcurrency < 16)
                {
                    currentConcurrency += 2;
                    processor1.SetMaxConcurrency(currentConcurrency);
                    processor2.SetMaxConcurrency(currentConcurrency);
                    globalSink.Raise($"concurrency.increased:to={currentConcurrency}:reason=good-performance");
                }
            }
        }, concurrencyCts.Token);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Processing 50 widgets...[/]");
        AnsiConsole.MarkupLine("[grey]Watch for failover when Processor 1 fails repeatedly[/]\n");

        // Process widgets through router
        for (int i = 0; i < 50; i++)
        {
            var widgetId = $"WIDGET-{i}";
            metrics.Total++;
            await router.EnqueueAsync(widgetId);
            await Task.Delay(100); // Stagger submissions
        }

        // Complete coordinators and wait for drain
        router.Complete();
        processor1.Complete();
        processor2.Complete();

        await router.DrainAsync();
        await processor1.DrainAsync();
        await processor2.DrainAsync();

        // Stop background tasks
        proberCts.Cancel();
        concurrencyCts.Cancel();

        try
        {
            await Task.WhenAll(probeTask, concurrencyTask);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Cleanup subscriptions
        visualizationSub.Dispose();

        // Display results
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Results[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Total Operations", $"[cyan]{metrics.Total}[/]");
        table.AddRow("Successful", $"[green]{metrics.Successful}[/]");
        table.AddRow("Failed", $"[red]{metrics.Failed}[/]");
        table.AddRow("Failovers Triggered", $"[yellow]{metrics.Failovers}[/]");
        table.AddRow("Success Rate", $"[cyan]{(metrics.Successful * 100.0 / metrics.Total):F1}%[/]");
        table.AddRow("Final Concurrency", $"[blue]{currentConcurrency}[/]");

        var proc1Stats = processor1.GetSnapshot();
        var proc2Stats = processor2.GetSnapshot();
        table.AddRow("Processor 1 Ops", $"[cyan]{proc1Stats.Count}[/]");
        table.AddRow("Processor 2 Ops", $"[cyan]{proc2Stats.Count}[/]");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Key Features Demonstrated:[/]");
        AnsiConsole.MarkupLine("  [green]✓[/] Priority-based failover (Primary → Backup)");
        AnsiConsole.MarkupLine("  [green]✓[/] Dynamic routing based on health signals");
        AnsiConsole.MarkupLine("  [green]✓[/] Self-healing via periodic probing");
        AnsiConsole.MarkupLine("  [green]✓[/] Adaptive concurrency adjustment");
        AnsiConsole.MarkupLine("  [green]✓[/] Full observability via signals");
        AnsiConsole.MarkupLine("  [green]✓[/] Using coordinators (NOT event handlers!)");
    }

    private class WorkflowMetrics
    {
        public int Total = 0;
        public int Successful = 0;
        public int Failed = 0;
        public int Failovers = 0;
    }
}
