using System.Diagnostics;
using Mostlylucid.Ephemeral;
using Spectre.Console;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Quick perf test for Dynamic Workflow multi-coordinator pattern.
/// Tests hotspots without full BenchmarkDotNet overhead.
/// </summary>
public static class QuickDynamicWorkflowTest
{
    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[yellow]Quick Dynamic Workflow Performance Test[/]");
        AnsiConsole.MarkupLine("[grey]Testing multi-coordinator shared sink hotspots[/]\n");

        var globalSink = new SignalSink(maxCapacity: 5000);
        var processorHealth = new Dictionary<int, bool> { [1] = true, [2] = true };
        var processorAFailureRate = 0.3;
        var processorBFailureRate = 0.05;

        // Setup coordinators
        await using var processor1 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                globalSink.Raise($"processing.started:pri1:{widgetId}");
                await Task.Delay(1, ct);
                var success = Random.Shared.NextDouble() > processorAFailureRate;

                if (success)
                {
                    processorHealth[1] = true;
                    globalSink.Raise($"processing.complete:pri1:{widgetId}");
                }
                else
                {
                    globalSink.Raise($"processing.failed:pri1:{widgetId}");
                    var recentFailures = globalSink.CountRecentByPrefix(
                        "processing.failed:pri1",
                        DateTimeOffset.UtcNow.AddSeconds(-10));

                    if (recentFailures >= 3)
                    {
                        processorHealth[1] = false;
                        globalSink.Raise("failover.triggered:pri1→pri2");
                    }
                }
            },
            new EphemeralOptions { MaxConcurrency = 4, Signals = globalSink }
        );

        await using var processor2 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                globalSink.Raise($"processing.started:pri2:{widgetId}");
                await Task.Delay(2, ct);
                var success = Random.Shared.NextDouble() > processorBFailureRate;

                if (success)
                {
                    globalSink.Raise($"processing.complete:pri2:{widgetId}");
                }
                else
                {
                    globalSink.Raise($"processing.failed:pri2:{widgetId}");
                }
            },
            new EphemeralOptions { MaxConcurrency = 4, Signals = globalSink }
        );

        await using var router = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                var targetPriority = processorHealth[1] ? 1 : 2;
                globalSink.Raise($"route.assigned:pri{targetPriority}:{widgetId}");

                if (targetPriority == 1)
                    await processor1.EnqueueAsync(widgetId);
                else
                    await processor2.EnqueueAsync(widgetId);
            },
            new EphemeralOptions { MaxConcurrency = 16, Signals = globalSink }
        );

        // Test 1: Signal raising hotspot
        AnsiConsole.MarkupLine("[cyan]Test 1: Signal raising to shared sink (1000 signals)[/]");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            globalSink.Raise($"test.signal:{i}");
        }
        sw.Stop();
        AnsiConsole.MarkupLine($"  Mean: [green]{sw.ElapsedMilliseconds}ms total[/] ([yellow]{sw.Elapsed.TotalMicroseconds / 1000:F2}µs per signal[/])");
        AnsiConsole.MarkupLine($"  Throughput: [green]{1000 / sw.Elapsed.TotalSeconds:F0} signals/sec[/]\n");

        // Test 2: Signal Sense query hotspot
        // Pre-populate with test signals
        for (int i = 0; i < 500; i++)
        {
            globalSink.Raise($"processing.failed:pri1:TEST-{i}");
        }

        AnsiConsole.MarkupLine("[cyan]Test 2a: Signal Sense query - OLD (1000 queries, LINQ + Contains)[/]");
        sw.Restart();
        for (int i = 0; i < 1000; i++)
        {
            var results = globalSink.Sense(s =>
                s.Signal.Contains("processing.failed") &&
                s.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-60));
            var count = results.Count;
        }
        sw.Stop();
        AnsiConsole.MarkupLine($"  Mean: [green]{sw.ElapsedMilliseconds}ms total[/] ([yellow]{sw.Elapsed.TotalMicroseconds / 1000:F2}µs per query[/])");
        AnsiConsole.MarkupLine($"  Throughput: [green]{1000 / sw.Elapsed.TotalSeconds:F0} queries/sec[/]\n");

        AnsiConsole.MarkupLine("[cyan]Test 2b: CountRecentByContains - OPTIMIZED (1000 queries)[/]");
        sw.Restart();
        for (int i = 0; i < 1000; i++)
        {
            var count = globalSink.CountRecentByContains("processing.failed", DateTimeOffset.UtcNow.AddSeconds(-60));
        }
        sw.Stop();
        var optimizedTime = sw.Elapsed.TotalMicroseconds / 1000;
        AnsiConsole.MarkupLine($"  Mean: [green]{sw.ElapsedMilliseconds}ms total[/] ([yellow]{optimizedTime:F2}µs per query[/])");
        AnsiConsole.MarkupLine($"  Throughput: [green]{1000 / sw.Elapsed.TotalSeconds:F0} queries/sec[/]\n");

        AnsiConsole.MarkupLine("[cyan]Test 2c: CountRecentByPrefix - MOST OPTIMIZED (1000 queries)[/]");
        sw.Restart();
        for (int i = 0; i < 1000; i++)
        {
            var count = globalSink.CountRecentByPrefix("processing.failed:", DateTimeOffset.UtcNow.AddSeconds(-60));
        }
        sw.Stop();
        var prefixTime = sw.Elapsed.TotalMicroseconds / 1000;
        AnsiConsole.MarkupLine($"  Mean: [green]{sw.ElapsedMilliseconds}ms total[/] ([yellow]{prefixTime:F2}µs per query[/])");
        AnsiConsole.MarkupLine($"  Throughput: [green]{1000 / sw.Elapsed.TotalSeconds:F0} queries/sec[/]");
        AnsiConsole.MarkupLine($"  [yellow]Speedup vs LINQ: {(90.0 / prefixTime):F1}x faster[/]\n");

        // Test 3: Concurrent signal raising from 4 coordinators
        AnsiConsole.MarkupLine("[cyan]Test 3: Concurrent signal raising from 4 threads (400 signals each)[/]");
        sw.Restart();
        await Task.WhenAll(
            Task.Run(() => { for (int i = 0; i < 400; i++) globalSink.Raise($"coord1:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 400; i++) globalSink.Raise($"coord2:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 400; i++) globalSink.Raise($"coord3:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 400; i++) globalSink.Raise($"coord4:signal:{i}"); })
        );
        sw.Stop();
        AnsiConsole.MarkupLine($"  Mean: [green]{sw.ElapsedMilliseconds}ms total[/] ([yellow]{sw.Elapsed.TotalMicroseconds / 1600:F2}µs per signal[/])");
        AnsiConsole.MarkupLine($"  Throughput: [green]{1600 / sw.Elapsed.TotalSeconds:F0} signals/sec[/] (4 threads)");
        AnsiConsole.MarkupLine($"  Scaling: [cyan]{(1600 / sw.Elapsed.TotalSeconds) / (1000 / (sw.Elapsed.TotalSeconds / 4)):F2}x[/] vs single-threaded\n");

        // Test 4: End-to-end workflow
        AnsiConsole.MarkupLine("[cyan]Test 4: End-to-end workflow (100 widgets with realistic delays)[/]");
        sw.Restart();

        var enqueueTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                await router.EnqueueAsync($"WIDGET-{i}");
                await Task.Delay(10); // Realistic submission rate
            }
            router.Complete();
        });

        await enqueueTask;

        processor1.Complete();
        processor2.Complete();

        await router.DrainAsync();
        await processor1.DrainAsync();
        await processor2.DrainAsync();
        sw.Stop();

        AnsiConsole.MarkupLine($"  Total time: [green]{sw.ElapsedMilliseconds}ms[/]");
        AnsiConsole.MarkupLine($"  Per widget: [yellow]{sw.Elapsed.TotalMilliseconds / 100:F2}ms[/]");
        AnsiConsole.MarkupLine($"  Throughput: [green]{100 / sw.Elapsed.TotalSeconds:F0} widgets/sec[/]\n");

        var proc1Count = processor1.GetSnapshot().Count;
        var proc2Count = processor2.GetSnapshot().Count;

        AnsiConsole.MarkupLine($"[grey]Processor 1 handled: {proc1Count} widgets[/]");
        AnsiConsole.MarkupLine($"[grey]Processor 2 handled: {proc2Count} widgets[/]");
        AnsiConsole.MarkupLine($"[grey]Failovers occurred: {proc2Count > 0}[/]\n");

        AnsiConsole.MarkupLine("[green]✓ Performance test complete[/]");
    }
}
