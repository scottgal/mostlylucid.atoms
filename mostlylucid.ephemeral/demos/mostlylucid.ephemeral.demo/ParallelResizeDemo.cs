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
                Console.Write($"Images: {imagesCompleted}/{imageCount} | ");
                Console.Write($"Resizing: {string.Join(", ", currentlyResizing.Take(3))}");
                if (currentlyResizing.Count > 3) Console.Write($" +{currentlyResizing.Count - 3} more");
                Console.Write($" | Completed: {completedResizes.Count} sizes");
            }
        });

        Console.WriteLine("Live Progress:");
        Console.WriteLine("────────────────────────────────────────────────────────────────");

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
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("\n\n[ESC] pressed - sending stop signals to all operations...");
                    sink.Raise("imagesharp.stop");  // Global stop signal
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(50);
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
            Console.WriteLine($"\n  🛑 Cancellation signals detected: {stopSignals}");
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
            }
        }

        // Verify outputs
        var outputFiles = Directory.GetFiles(outputDir, "*.jpg");
        Console.WriteLine($"\n  Output files created: {outputFiles.Length}");
        Console.WriteLine($"  Expected files: {processedCount * resizeOptions.Sizes.Count}");

        var totalSize = outputFiles.Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"  Total output size: {FormatBytes(totalSize)}");

        Console.WriteLine($"\n💡 Key Patterns Demonstrated:");
        Console.WriteLine($"   ✓ Each resize operation gets its own operation ID");
        Console.WriteLine($"   ✓ Bounded parallelism: {resizeOptions.MaxParallelism} concurrent resizes");
        Console.WriteLine($"   ✓ Signal-based cancellation via [ESC] → 'imagesharp.stop'");
        Console.WriteLine($"   ✓ Nested coordinators propagate operation IDs automatically");
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
