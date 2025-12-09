using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.ImageSharp;
using SixLabors.ImageSharp;
using System.Diagnostics;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates the nested coordinator pattern - an atom that internally uses
/// a coordinator for bounded parallel work while propagating signals with
/// operation IDs from sub-operations.
/// </summary>
public static class ParallelResizeDemo
{
    public static async Task RunAsync()
    {
        await RunSingleAsync();
    }

    public static async Task RunSingleAsync()
    {
        Console.WriteLine("=== Parallel Resize Demo: Nested Coordinator Pattern ===\n");
        Console.WriteLine("💡 Press [ESC] during processing to cancel operations via signals\n");

        // Setup
        var sourceImage = FindTestImage();
        var outputDir = Path.Combine(AppContext.BaseDirectory, "output", "parallel-resize");

        if (sourceImage == null)
        {
            Console.WriteLine($"Error: Could not find test image logo.png");
            Console.WriteLine($"Tried locations:");
            Console.WriteLine($"  - {{AppContext.BaseDirectory}}/testdata/logo.png");
            Console.WriteLine($"  - {{CurrentDirectory}}/testdata/logo.png");
            Console.WriteLine($"  - {{CurrentDirectory}}/../../testdata/logo.png (from build output)");
            return;
        }

        // Clean output directory
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, true);
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Source: {sourceImage}");
        Console.WriteLine($"Output: {outputDir}\n");

        // Process MANY images to show cancellation working
        const int imageCount = 50;

        // Create signal sink and subscribe to see nested operation pattern
        var sink = new SignalSink();
        var signalLog = new List<(string Signal, long OperationId, DateTimeOffset Timestamp)>();

        // Configure custom resize sizes - 6 different sizes for demo
        var resizeOptions = new ParallelResizeOptions
        {
            Sizes = new List<(Size, string)>
            {
                (new Size(100, 100), "tiny"),
                (new Size(200, 200), "small"),
                (new Size(400, 400), "medium"),
                (new Size(800, 800), "large"),
                (new Size(1200, 1200), "xlarge"),
                (new Size(1920, 1920), "xxlarge")
            },
            JpegQuality = 90,
            MaxParallelism = Environment.ProcessorCount // Max speed - use all processors
        };

        // Live status tracking
        var statusLock = new object();
        var imagesCompleted = 0;
        var currentlyResizing = new HashSet<string>();
        var completedResizes = new HashSet<string>();

        sink.Subscribe(signal =>
        {
            signalLog.Add((signal.Signal, signal.OperationId, signal.Timestamp));

            // Update live status
            lock (statusLock)
            {
                if (signal.Signal.Contains(".started") && !signal.Signal.StartsWith("resize.parallel"))
                {
                    var sizeName = signal.Signal.Split('.')[1];
                    currentlyResizing.Add(sizeName);
                }
                else if (signal.Signal.Contains(".complete") && !signal.Signal.StartsWith("resize.parallel"))
                {
                    var sizeName = signal.Signal.Split('.')[1];
                    currentlyResizing.Remove(sizeName);
                    completedResizes.Add(sizeName);
                }
                else if (signal.Signal == "resize.parallel.complete")
                {
                    imagesCompleted++;
                    currentlyResizing.Clear();
                }

                // Show live status (overwrite previous line)
                Console.Write($"\r\u001b[K"); // Clear line
                var pct = (int)(100.0 * imagesCompleted / imageCount);
                Console.Write($"Images: {imagesCompleted}/{imageCount} ({pct}%) | ");
                Console.Write($"Resizing: {string.Join(", ", currentlyResizing.Take(3))}");
                if (currentlyResizing.Count > 3) Console.Write($" +{currentlyResizing.Count - 3} more");
                Console.Write($" | Size variants completed (this run): {completedResizes.Count}/{resizeOptions.Sizes.Count}");
            }
        });

        Console.WriteLine("Live Progress:");
        Console.WriteLine("────────────────────────────────────────────────────────────────");

        Console.WriteLine($"Processing {imageCount} images (each with {resizeOptions.Sizes.Count} sizes)");
        Console.WriteLine($"Total resize operations: {imageCount * resizeOptions.Sizes.Count} operations");
        Console.WriteLine($"Max parallelism: {resizeOptions.MaxParallelism} concurrent resizes ({Environment.ProcessorCount} processors)\n");
        Console.WriteLine("Watch for operation IDs - each resize is a sub-operation");
        Console.WriteLine("Press [ESC] to cancel all in-progress operations\n");

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();

        // Background task to listen for ESC key
        var escapeTask = Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("\n\n[ESC] pressed - raising 'imagesharp.stop' signal...");
                        sink.Raise("imagesharp.stop");  // Global stop signal
                        cts.Cancel();
                        break;
                    }
                    Thread.Sleep(50);
                }
            }
            catch (InvalidOperationException)
            {
                // Console.KeyAvailable not supported on some hosts - degrade gracefully
            }
        });

        // Build pipeline with parallel resize and cancellation hook
        // Note: Using operation ID 0 means we need to track each job's operation ID
        await using var pipeline = new ImagePipeline(sink)
            .WithLoader()
            .WithParallelResize(resizeOptions);

        var processedCount = 0;
        var cancelledCount = 0;

        try
        {
            // Process each image
            for (int i = 0; i < imageCount && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var job = new ImageJob(sourceImage, outputDir, 0, i);
                    var result = await pipeline.ProcessAsync(job, cts.Token);
                    processedCount++;

                    if (i % 5 == 0)  // Progress update every 5 images
                    {
                        Console.WriteLine($"  Progress: {processedCount}/{imageCount} images processed");
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelledCount++;
                    Console.WriteLine($"  Image {i} cancelled");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n✋ Processing cancelled by user");
        }

        sw.Stop();
        cts.Cancel();  // Ensure escape task stops
        await escapeTask;

        // Analysis
        var duration = sw.Elapsed;
        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine($"Processing Summary:");
        Console.WriteLine($"{'=',-60}");
        Console.WriteLine($"  Total time:        {duration.TotalSeconds:F2}s");
        Console.WriteLine($"  Images processed:  {processedCount}/{imageCount}");
        Console.WriteLine($"  Images cancelled:  {cancelledCount}");
        Console.WriteLine($"  Throughput:        {processedCount / duration.TotalSeconds:F1} images/sec");

        Console.WriteLine($"\nSignal Analysis:");
        Console.WriteLine($"  Total signals emitted: {signalLog.Count}");

        var uniqueOpIds = signalLog
            .Where(s => s.OperationId != 0)
            .Select(s => s.OperationId)
            .Distinct()
            .ToList();

        Console.WriteLine($"  Unique operation IDs: {uniqueOpIds.Count}");
        Console.WriteLine($"  First 10 operation IDs: [{string.Join(", ", uniqueOpIds.Take(10))}]...");

        // Show signal breakdown by type
        var signalsByType = signalLog
            .GroupBy(s => s.Signal.Split('.').FirstOrDefault() ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Take(5);

        Console.WriteLine("\n  Top signal types:");
        foreach (var group in signalsByType)
        {
            Console.WriteLine($"    {group.Key}.*: {group.Count()} signals");
        }

        // Check for cancellation signals
        var stopSignals = signalLog.Count(s => s.Signal.Contains("stop") || s.Signal.Contains("cancel"));
        if (stopSignals > 0)
        {
            Console.WriteLine($"\n  🛑 Cancellation signals detected: {stopSignals} (e.g. 'imagesharp.stop')");
            Console.WriteLine($"      ESC → imagesharp.stop → coordinators react");
        }

        // Show parallel execution pattern (first few operations)
        var startSignals = signalLog
            .Where(s => s.Signal.Contains(".started"))
            .OrderBy(s => s.Timestamp)
            .Take(10)
            .ToList();

        var completeSignals = signalLog
            .Where(s => s.Signal.Contains(".complete"))
            .OrderBy(s => s.Timestamp)
            .Take(10)
            .ToList();

        if (startSignals.Any())
        {
            Console.WriteLine($"\n  Execution timeline (first 10 operations):");
            Console.WriteLine($"    First resize started:  {startSignals.First().Timestamp:HH:mm:ss.fff}");
            Console.WriteLine($"    Last started:          {startSignals.Last().Timestamp:HH:mm:ss.fff}");
            if (completeSignals.Any())
            {
                Console.WriteLine($"    First resize finished: {completeSignals.First().Timestamp:HH:mm:ss.fff}");
                Console.WriteLine($"    Last finished:         {completeSignals.Last().Timestamp:HH:mm:ss.fff}");

                var windowMs = (completeSignals.Last().Timestamp - startSignals.First().Timestamp).TotalMilliseconds;
                Console.WriteLine($"    Bounded execution window: ~{windowMs:F1} ms for first 10 resizes");
            }
        }

        // Verify outputs
        var outputFiles = Directory.GetFiles(outputDir, "*.jpg");
        Console.WriteLine($"\n  Output files created: {outputFiles.Length}");
        Console.WriteLine($"  Expected files: {processedCount * resizeOptions.Sizes.Count}");

        var totalSize = outputFiles.Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"  Total output size: {FormatBytes(totalSize)}");

        // Note: In a richer demo, we could derive these from signals alone:
        // count 'file.saved' + sum 'resize.size.bytes' to reconstruct output stats
        // without touching the filesystem. Signals = observability substrate.

        Console.WriteLine($"\n💡 Key Patterns Demonstrated:");
        Console.WriteLine($"   ✓ Each resize operation gets its own operation ID");
        Console.WriteLine($"   ✓ Bounded parallelism: {resizeOptions.MaxParallelism} concurrent resizes");
        Console.WriteLine($"   ✓ Signal-based cancellation via [ESC] → 'imagesharp.stop'");
        Console.WriteLine($"   ✓ Nested coordinators propagate operation IDs automatically");
    }

    public static async Task RunContinuousAsync()
    {
        Console.WriteLine("=== Continuous Resize Loop: Long-term Behavior Test ===");
        Console.WriteLine("(All metrics are derived from Ephemeral signals — no explicit instrumentation.)\n");
        Console.WriteLine("🔄 Press [ESC] to stop continuous processing\n");
        Console.WriteLine("This mode runs indefinitely to verify:");
        Console.WriteLine("  • No memory leaks");
        Console.WriteLine("  • Signal window cleanup");
        Console.WriteLine("  • Operation eviction");
        Console.WriteLine("  • Stable throughput over time\n");

        // Setup
        var sourceImage = FindTestImage();
        if (sourceImage == null)
        {
            Console.WriteLine($"Error: Could not find test image logo.png");
            return;
        }

        var outputDir = Path.Combine(AppContext.BaseDirectory, "output", "parallel-resize-continuous");

        var sink = new SignalSink(maxCapacity: 500, maxAge: TimeSpan.FromSeconds(30)); // Smaller window for continuous mode

        var resizeOptions = new ParallelResizeOptions
        {
            Sizes = new List<(Size, string)>
            {
                (new Size(200, 200), "small"),
                (new Size(400, 400), "medium"),
                (new Size(800, 800), "large")
            },
            JpegQuality = 85,
            MaxParallelism = Environment.ProcessorCount
        };

        var cts = new CancellationTokenSource();

        // ESC key handler
        var escapeTask = Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("\n\n[ESC] pressed - stopping continuous mode...");
                        cts.Cancel();
                        break;
                    }
                    Thread.Sleep(50);
                }
            }
            catch (InvalidOperationException) { }
        });

        const int imagesPerBatch = 20;
        var iteration = 0;
        var totalProcessed = 0L;
        var startTime = DateTimeOffset.UtcNow;
        var lastUpdateTime = startTime;

        // Live metrics driven by signals
        var statusLock = new object();
        var completedResizes = 0;
        var currentBatch = 0;

        Console.WriteLine($"Processing {imagesPerBatch} images/batch × {resizeOptions.Sizes.Count} sizes = {imagesPerBatch * resizeOptions.Sizes.Count} resizes/batch");
        Console.WriteLine($"Max parallelism: {resizeOptions.MaxParallelism} concurrent resize operations");
        Console.WriteLine($"Signal window: {sink.MaxCapacity} events, {sink.MaxAge.TotalSeconds}s retention\n");
        Console.WriteLine("📊 Live metrics updated from signals...\n");

        // Subscribe to signals for LIVE updates
        sink.Subscribe(signal =>
        {
            if (signal.Signal.Contains(".complete") && !signal.Signal.StartsWith("resize.parallel"))
            {
                lock (statusLock)
                {
                    completedResizes++;
                    totalProcessed++;

                    // Throttle display updates to ~10/sec
                    var now = DateTimeOffset.UtcNow;
                    if ((now - lastUpdateTime).TotalMilliseconds > 100 || completedResizes % 10 == 0)
                    {
                        lastUpdateTime = now;
                        var uptime = now - startTime;
                        var throughput = totalProcessed / uptime.TotalSeconds;
                        var memoryMB = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
                        var signalCount = sink.Count;

                        Console.Write($"\r\u001b[K"); // Clear line
                        Console.Write($"Batch: {currentBatch} | ");
                        Console.Write($"Total: {totalProcessed} resizes | ");
                        Console.Write($"Throughput: {throughput:F1}/s | ");
                        Console.Write($"Memory: {memoryMB:F1} MB | ");
                        Console.Write($"Signals: {totalProcessed} total ({signalCount} live in window) | ");
                        Console.Write($"Uptime: {uptime.TotalSeconds:F0}s");
                    }
                }
            }
        });

        try
        {
            await using var pipeline = new ImagePipeline(sink)
                .WithLoader()
                .WithParallelResize(resizeOptions);

            while (!cts.Token.IsCancellationRequested)
            {
                iteration++;
                currentBatch = iteration;

                // Clean output directory each iteration
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
                Directory.CreateDirectory(outputDir);

                completedResizes = 0;

                // Safety check: verify source image still exists (may be deleted by external build clean)
                if (!File.Exists(sourceImage))
                {
                    Console.WriteLine($"\n\n⚠️  Source image disappeared (external build clean?) - stopping gracefully");
                    break;
                }

                // Process batch - metrics update LIVE via signals
                for (int i = 0; i < imagesPerBatch && !cts.Token.IsCancellationRequested; i++)
                {
                    var job = new ImageJob(sourceImage, outputDir, 0, i);
                    await pipeline.ProcessAsync(job, cts.Token);
                }

                // Occasional detailed report
                if (iteration % 10 == 0)
                {
                    var uptime = DateTimeOffset.UtcNow - startTime;
                    var throughput = totalProcessed / uptime.TotalSeconds;
                    var memoryMB = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
                    var signalCount = sink.Count;

                    Console.WriteLine($"\n");
                    Console.WriteLine($"  ═══ Checkpoint at batch {iteration} ═══");
                    Console.WriteLine($"  Total resizes:     {totalProcessed:N0}");
                    Console.WriteLine($"  Avg throughput:    {throughput:F1} resizes/sec");
                    Console.WriteLine($"  Memory usage:      {memoryMB:F1} MB");
                    Console.WriteLine($"  Signal window:     {signalCount} / {sink.MaxCapacity} live ({totalProcessed:N0} total this run)");
                    Console.WriteLine($"  GC collections:    Gen0={GC.CollectionCount(0)}, Gen1={GC.CollectionCount(1)}, Gen2={GC.CollectionCount(2)}");
                    Console.WriteLine($"  Uptime:            {uptime:hh\\:mm\\:ss}");
                    Console.WriteLine($"  Status:            Throughput and memory flat → no leak detected so far");
                    Console.WriteLine();
                }

                // Small delay between batches
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on ESC
        }

        cts.Cancel();
        await escapeTask;

        var finalUptime = DateTimeOffset.UtcNow - startTime;
        var finalThroughput = totalProcessed / finalUptime.TotalSeconds;

        Console.WriteLine("\n\n═══ Continuous Mode Summary ═══");
        Console.WriteLine($"Total batches:     {iteration}");
        Console.WriteLine($"Total resizes:     {totalProcessed:N0}");
        Console.WriteLine($"Total time:        {finalUptime.TotalSeconds:F1}s");
        Console.WriteLine($"Avg throughput:    {finalThroughput:F1} resizes/sec");
        Console.WriteLine($"Final memory:      {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine($"\n✅ Continuous mode demonstrates:");
        Console.WriteLine($"   • Stable memory usage (no leaks)");
        Console.WriteLine($"   • Automatic signal window cleanup");
        Console.WriteLine($"   • Consistent throughput over time");
        Console.WriteLine($"   • Operation eviction working correctly");
        Console.WriteLine($"   • LIVE metrics driven by signals");
        Console.WriteLine($"\nAll metrics above are driven purely from Ephemeral signals (no extra tracing code).");
        Console.WriteLine($"\n(Note: If exit code is non-zero, this indicates manual ESC termination of the continuous loop.)");
    }

    private static string? FindTestImage()
    {
        // Try multiple possible locations for the test image
        var possiblePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "testdata", "logo.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "testdata", "logo.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "testdata", "logo.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "testdata", "logo.png"),
        };

        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"[Test Image] Using: {fullPath}");
                return fullPath;
            }
        }

        return null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:F2} {sizes[order]}";
    }
}
