using System.Diagnostics;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
///     Demonstrates the "preload and trigger" pattern using EnqueueManyAsync + DeferOnSignals + ResumeOnSignals.
///
///     Pattern: Defer while "batch.loading" signal exists, bulk enqueue, then raise "batch.ready" to trigger.
///     This is the proper architectural pattern for batch loading scenarios.
/// </summary>
public static class PreloadDemo
{
    public record ImageProcessingJob(int Id, string ImagePath, string Operation);

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Preload and Trigger Demo ===\n");
        Console.WriteLine("Demonstrating: DeferOnSignals + EnqueueManyAsync + ResumeOnSignals\n");

        // Shared signal sink
        var signals = new SignalSink(maxCapacity: 2000);

        // Step 1: Raise defer signal BEFORE creating coordinator
        signals.Raise("batch.loading");
        Console.WriteLine("[1] Raised 'batch.loading' signal - work will be deferred");

        // Small delay to ensure signal propagates
        await Task.Delay(50);
        Console.WriteLine("    Signal propagated to sink\n");

        // Create coordinator with Defer/Resume signals
        var coordinator = new EphemeralWorkCoordinator<ImageProcessingJob>(
            ProcessImageAsync,
            new EphemeralOptions
            {
                MaxConcurrency = 4,

                // CRITICAL: MaxTrackedOperations controls the internal channel capacity.
                // When using DeferOnSignals, the consumer won't read from the channel while deferred,
                // so the channel can fill up during bulk enqueue. Set this >= the number of jobs
                // you plan to enqueue to prevent EnqueueManyAsync from blocking.
                MaxTrackedOperations = 1500, // Must be >= 1000 jobs we're enqueueing

                DeferOnSignals = new HashSet<string> { "batch.loading" },
                ResumeOnSignals = new HashSet<string> { "batch.ready", "batch.complete" },
                Signals = signals
            });

        // Step 2: Bulk enqueue 1000 jobs - they won't process yet
        var stopwatch = Stopwatch.StartNew();
        var jobs = Enumerable.Range(1, 1000)
            .Select(i => new ImageProcessingJob(i, $"/images/img_{i}.jpg", "resize"))
            .ToList();

        Console.WriteLine($"    About to enqueue {jobs.Count} jobs...");
        var enqueued = await coordinator.EnqueueManyAsync(jobs);
        stopwatch.Stop();
        Console.WriteLine($"    Enqueue completed, took {stopwatch.ElapsedMilliseconds}ms");

        Console.WriteLine($"[2] Bulk enqueued {enqueued} jobs in {stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"    Jobs are queued but NOT processing (deferred by 'batch.loading' signal)\n");

        // Monitor pending count (should stay at 1000 while deferred)
        Console.WriteLine($"    Waiting 500ms to verify defer is working...");
        await Task.Delay(500);
        var completed = coordinator.GetCompleted().Count;
        var running = coordinator.GetRunning().Count;
        Console.WriteLine($"[3] Status check: {coordinator.PendingCount} jobs pending, {completed} completed, {running} running");
        Console.WriteLine($"    (All {enqueued} jobs should be waiting for 'batch.ready' signal)");

        if (completed > 0 || running > 0)
        {
            Console.WriteLine($"    [ERROR] Jobs started processing! Defer is NOT working!");
            Console.WriteLine($"    Completed: {completed}, Running: {running}");
        }
        else
        {
            Console.WriteLine($"    [SUCCESS] Defer is working - no jobs processing yet!");
        }
        Console.WriteLine();

        // Step 4: TRIGGER - Raise resume signal to start processing
        Console.WriteLine("[4] TRIGGER: Raising 'batch.ready' signal - processing starts NOW!\n");
        signals.Raise("batch.ready");

        // Start real-time monitoring of results
        var monitorTask = MonitorProgressAsync(coordinator, signals);

        // Watch the magic happen - jobs process in real-time
        await monitorTask;

        // Final stats
        var finalCompleted = coordinator.GetCompleted().Count;
        var finalFailed = coordinator.GetFailed().Count;
        var allOps = coordinator.GetSnapshot();
        var maxDuration = allOps.Where(o => o.Duration.HasValue).Any()
            ? allOps.Where(o => o.Duration.HasValue).Max(o => o.Duration!.Value.TotalMilliseconds)
            : 0.0;
        Console.WriteLine($"\n[5] Final: {finalCompleted} completed, {finalFailed} failed");
        Console.WriteLine($"    Max duration: {maxDuration:F0}ms\n");

        await coordinator.DisposeAsync();
        Console.WriteLine("=== Demo Complete ===\n");
    }

    private static async Task ProcessImageAsync(ImageProcessingJob job, CancellationToken ct)
    {
        // Simulate image processing work
        await Task.Delay(Random.Shared.Next(50, 150), ct);

        // Simulate occasional failures for demo purposes
        if (Random.Shared.Next(100) < 2) // 2% failure rate
        {
            throw new InvalidOperationException($"Failed to process image {job.Id}");
        }
    }

    private static async Task MonitorProgressAsync(
        EphemeralWorkCoordinator<ImageProcessingJob> coordinator,
        SignalSink signals)
    {
        var lastCompleted = 0;
        var spinner = new[] { '|', '/', '-', '\\' };
        var spinnerIndex = 0;

        while (true)
        {
            var completedCount = coordinator.GetCompleted().Count;
            var runningCount = coordinator.GetRunning().Count;
            var failedCount = coordinator.GetFailed().Count;

            if (completedCount != lastCompleted)
            {
                var progress = (int)((completedCount / 1000.0) * 40);
                var bar = new string('█', progress) + new string('░', 40 - progress);

                Console.Write($"\r    {spinner[spinnerIndex++ % 4]} [{bar}] {completedCount,4}/1000 " +
                             $"| Active: {runningCount,2} | Failed: {failedCount,2}");

                lastCompleted = completedCount;
            }

            if (coordinator.PendingCount == 0 && runningCount == 0)
            {
                Console.WriteLine(); // New line after progress bar
                break;
            }

            await Task.Delay(50);
        }
    }
}
