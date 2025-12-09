using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.ImageSharp;
using System.Diagnostics;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates realistic multi-stage image processing with file I/O,
/// resizing, EXIF manipulation, and watermarking.
/// </summary>
public static class ImageProcessingDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Image Processing Pipeline Demo ===\n");

        // Setup: Clear and create output directory
        var outputDir = Path.Combine(AppContext.BaseDirectory, "output", "images");
        if (Directory.Exists(outputDir))
        {
            Console.WriteLine($"Clearing output directory: {outputDir}");
            Directory.Delete(outputDir, true);
        }
        Directory.CreateDirectory(outputDir);

        // Find source image - try multiple locations
        var sourceImage = FindTestImage();
        if (sourceImage == null)
        {
            Console.WriteLine($"Error: Could not find test image logo.png");
            Console.WriteLine($"Tried locations:");
            Console.WriteLine($"  - {{AppContext.BaseDirectory}}/testdata/logo.png");
            Console.WriteLine($"  - {{CurrentDirectory}}/testdata/logo.png");
            Console.WriteLine($"  - {{CurrentDirectory}}/../../testdata/logo.png (from build output)");
            return;
        }

        Console.WriteLine($"Source image: {sourceImage}");
        Console.WriteLine($"Output directory: {outputDir}\n");

        // Create processing jobs (duplicate the source image for demo purposes)
        const int batchCount = 3;
        const int imagesPerBatch = 10;
        var jobs = new List<ImageJob>();

        for (int batch = 0; batch < batchCount; batch++)
        {
            for (int img = 0; img < imagesPerBatch; img++)
            {
                jobs.Add(new ImageJob(sourceImage, outputDir, batch, img));
            }
        }

        Console.WriteLine($"Processing {jobs.Count} images ({batchCount} batches × {imagesPerBatch} images)");
        Console.WriteLine($"Pipeline: Load → Resize (3 sizes) → EXIF → Watermark");
        Console.WriteLine($"\n💡 Press [ESC] to cancel processing\n");

        var sw = Stopwatch.StartNew();

        // Create shared signal sink for all operations
        var sink = new SignalSink();

        // Subscribe to show all signals in real-time
        var signalCount = 0;
        sink.Subscribe(signal =>
        {
            signalCount++;
            var timestamp = signal.Timestamp.ToString("HH:mm:ss.fff");
            var opId = signal.OperationId != 0 ? $" [op:{signal.OperationId}]" : "";
            var color = signal.Signal switch
            {
                var s when s.StartsWith("image.loading") => "\u001b[36m",      // Cyan
                var s when s.StartsWith("image.loaded") => "\u001b[32m",       // Green
                var s when s.StartsWith("resize.") => "\u001b[33m",            // Yellow
                var s when s.StartsWith("exif.") => "\u001b[35m",              // Magenta
                var s when s.StartsWith("watermark.") => "\u001b[34m",         // Blue
                var s when s.StartsWith("file.saved") => "\u001b[32m",         // Green
                var s when s.Contains("complete") => "\u001b[92m",             // Bright green
                _ => "\u001b[90m"                                               // Gray
            };
            Console.WriteLine($"\u001b[90m[{timestamp}]\u001b[0m {color}{signal.Signal}{opId}\u001b[0m");
        });

        Console.WriteLine("Signal Stream (real-time):");
        Console.WriteLine("────────────────────────────────────────────────────────────────");

        // Build the fluent image pipeline (ImageSharp-like API with Ephemeral signals)
        await using var pipeline = new ImagePipeline(sink)
            .WithLoader()
            .WithResize()
            .WithExif()
            .WithWatermark();

        // Setup cancellation token source
        var cts = new CancellationTokenSource();

        // Background task to listen for ESC key
        var escapeTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("\n\n\u001b[91m[ESC] pressed - cancelling all operations...\u001b[0m\n");
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(50);
            }
        });

        // Process all jobs with bounded parallelism
        var results = new List<ImageProcessingResult>();
        var semaphore = new SemaphoreSlim(4, 4); // Max 4 concurrent operations
        var processedCount = 0;
        var cancelledCount = 0;

        try
        {
            var tasks = jobs.Select(async job =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    var result = await pipeline.ProcessAsync(job, cts.Token);
                    Interlocked.Increment(ref processedCount);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelledCount);
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var completed = await Task.WhenAll(tasks);
            results.AddRange(completed);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"\u001b[93m✋ Processing cancelled by user\u001b[0m");
        }

        sw.Stop();
        cts.Cancel();  // Ensure escape task stops
        await escapeTask;

        // Display results
        Console.WriteLine("\n────────────────────────────────────────────────────────────────");
        Console.WriteLine($"\n✓ Processing complete in {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Images processed:  {processedCount}/{jobs.Count}");
        Console.WriteLine($"  Images cancelled:  {cancelledCount}");
        Console.WriteLine($"  Total signals:     {signalCount}");
        if (processedCount > 0)
        {
            Console.WriteLine($"  Throughput:        {processedCount / sw.Elapsed.TotalSeconds:F1} images/sec");
            Console.WriteLine($"  Per-image average: {sw.Elapsed.TotalMilliseconds / processedCount:F0}ms");
        }
        Console.WriteLine();

        if (results.Any())
        {
            // Calculate statistics
            var totalInputSize = results.Sum(r => r.OriginalSize);
            var totalOutputSize = results.Sum(r => r.TotalOutputSize);
            var avgProcessingTime = results.Average(r => r.ProcessingTime.TotalMilliseconds);

            Console.WriteLine("Statistics:");
            Console.WriteLine($"  Total input size:  {FormatBytes(totalInputSize)}");
            Console.WriteLine($"  Total output size: {FormatBytes(totalOutputSize)}");
            Console.WriteLine($"  Size multiplier:   {(double)totalOutputSize / totalInputSize:F1}x");
            Console.WriteLine($"  Avg processing:    {avgProcessingTime:F0}ms per image");
            Console.WriteLine($"  Files created:     {results.Count * 4} (thumb + medium + large + watermarked)");

            // Show sample output paths
            var sample = results.First();
            Console.WriteLine($"\nSample output paths (batch 0, image 0):");
            Console.WriteLine($"  Thumbnail:   {sample.ThumbnailPath}");
            Console.WriteLine($"  Medium:      {sample.MediumPath}");
            Console.WriteLine($"  Large:       {sample.LargePath}");
            Console.WriteLine($"  Watermarked: {sample.WatermarkedPath}");

            Console.WriteLine($"\nAll files saved to: {outputDir}");
        }
        else
        {
            Console.WriteLine("No images were processed (all cancelled).");
        }
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
