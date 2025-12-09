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

        // Find source image
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "testdata");
        var sourceImage = Path.Combine(testDataDir, "logo.png");

        if (!File.Exists(sourceImage))
        {
            Console.WriteLine($"Error: Source image not found at {sourceImage}");
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
        Console.WriteLine($"Pipeline: Load → Resize (3 sizes) → EXIF → Watermark\n");

        var sw = Stopwatch.StartNew();

        // Create shared signal sink for all operations
        var sink = new SignalSink();

        // Build the fluent image pipeline (ImageSharp-like API with Ephemeral signals)
        await using var pipeline = new ImagePipeline(sink)
            .WithLoader()
            .WithResize()
            .WithExif()
            .WithWatermark();

        // Process all jobs with bounded parallelism using manual concurrency control
        var results = new List<ImageProcessingResult>();
        var semaphore = new SemaphoreSlim(4, 4); // Max 4 concurrent operations
        var tasks = jobs.Select(async job =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await pipeline.ProcessAsync(job);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var completed = await Task.WhenAll(tasks);
        results.AddRange(completed);

        sw.Stop();

        // Display results
        Console.WriteLine($"\n✓ Processing complete in {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Throughput: {jobs.Count / sw.Elapsed.TotalSeconds:F1} images/sec");
        Console.WriteLine($"  Per-image average: {sw.Elapsed.TotalMilliseconds / jobs.Count:F0}ms\n");

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
