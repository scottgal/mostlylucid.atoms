# Mostlylucid.Ephemeral.Atoms.ImageSharp

**ImageSharp integration atoms for signal-based image processing pipelines**

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


This package provides production-ready atoms for image processing using ImageSharp with Ephemeral's signal-based coordination.

## Features

- **Signal-based pipeline** - Every operation emits signals for observability
- **Fluent API** - ImageSharp-like builder pattern
- **Configurable atoms** - Each atom has rich configuration options
- **Resource management** - Proper disposal of images and contexts
- **State tracking** - Processing context flows through pipeline

## Atoms

### LoadImageAtom
Loads images from disk with file I/O tracking.

**Signals:**
- `image.loading` - Before load starts
- `image.loaded` - After successful load
- `image.dimensions:{width}x{height}` - Image size
- `image.format:{format}` - Image format
- `image.load.failed` - On error
- `image.error:{message}` - Error details

### ResizeImageAtom
Creates multiple sized variants (thumbnail, medium, large) sequentially.

**Signals:**
- `resize.started` - Pipeline begins
- `resize.count:{n}` - Number of sizes
- `resize.{size}.started` - Per-size start
- `resize.{size}.complete` - Per-size completion
- `file.saved:{path}` - File written
- `resize.complete` - All sizes done

**Configuration:**
```csharp
new ResizeOptions
{
    Sizes = new List<(Size, string)>
    {
        (new Size(150, 150), "thumb"),
        (new Size(800, 600), "medium"),
        (new Size(1920, 1080), "large")
    },
    JpegQuality = 90
}
```

### ParallelResizeImageAtom
**Nested Coordinator Pattern** - Creates multiple sized variants using bounded parallel execution. Demonstrates how an atom can internally use a coordinator to manage concurrent work while propagating operation-scoped signals.

**Signals:**
- `resize.parallel.started` - Parallel pipeline begins
- `resize.parallelism:{n}` - Max parallel resizes
- `resize.{size}.started` - Per-size start (with sub-operation ID)
- `resize.{size}.complete` - Per-size completion (with sub-operation ID)
- `file.saved:{path}` - File written (with sub-operation ID)
- `resize.parallel.complete` - All sizes done

**Configuration:**
```csharp
new ParallelResizeOptions
{
    Sizes = new List<(Size, string)>
    {
        (new Size(150, 150), "thumb"),
        (new Size(800, 600), "medium"),
        (new Size(1920, 1080), "large")
    },
    JpegQuality = 90,
    MaxParallelism = 3  // Process 3 resizes concurrently
}
```

**Pattern Highlight:** Each resize operation becomes a sub-operation with its own operation ID. The coordinator window is configured as `maxParallelism * 3` for short-lived operations. Signals from sub-operations propagate to the main SignalSink with proper operation IDs, enabling precise tracking and control.

### ExifProcessingAtom
Adds EXIF metadata to images.

**Signals:**
- `exif.processing` - Processing started
- `exif.{size}.started` - Per-image start
- `exif.{size}.complete` - Per-image done
- `exif.written:{size}` - Metadata written
- `exif.complete` - All images processed

**Configuration:**
```csharp
new ExifOptions
{
    Copyright = "© 2025 Your Company",
    Software = "Your App Name",
    Artist = "Artist Name",
    Keywords = new List<string> { "keyword1", "keyword2" }
}
```

### WatermarkAtom
Adds text watermarks to images.

**Signals:**
- `watermark.started` - Processing begins
- `watermark.rendering` - Watermark being drawn
- `watermark.complete` - Watermark added
- `watermark.applied:{path}` - File saved
- `processing.complete` - Pipeline complete
- `image.pipeline.complete:{n}` - Image number

**Configuration:**
```csharp
new WatermarkOptions
{
    Text = "Your Watermark",
    FontFamily = "Arial",
    FontSize = 48,
    Color = (255, 255, 255),
    Opacity = 128,
    Quality = 95,
    TargetSize = "large",
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Bottom,
    Padding = 80f
}
```

## Quick Start

### Basic Pipeline

```csharp
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.ImageSharp;

var sink = new SignalSink();

await using var pipeline = new ImagePipeline(sink)
    .WithLoader()
    .WithResize()
    .WithExif()
    .WithWatermark();

var job = new ImageJob(
    sourcePath: "input.jpg",
    outputDir: "output",
    batchNumber: 0,
    imageNumber: 1
);

var result = await pipeline.ProcessAsync(job);

Console.WriteLine($"Processed in {result.ProcessingTime}");
Console.WriteLine($"Thumbnail: {result.ThumbnailPath}");
Console.WriteLine($"Medium: {result.MediumPath}");
Console.WriteLine($"Large: {result.LargePath}");
Console.WriteLine($"Watermarked: {result.WatermarkedPath}");
```

### Custom Configuration

```csharp
await using var pipeline = new ImagePipeline(sink)
    .WithLoader()
    .WithResize(new ResizeOptions
    {
        Sizes = new List<(Size, string)>
        {
            (new Size(200, 200), "small"),
            (new Size(1024, 768), "hd")
        },
        JpegQuality = 95
    })
    .WithExif(new ExifOptions
    {
        Copyright = "© 2025 MyCompany",
        Artist = "Photo Bot"
    })
    .WithWatermark(new WatermarkOptions
    {
        Text = "MyBrand",
        FontSize = 60,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom
    });
```

### Parallel Resize (Nested Coordinator Pattern)

```csharp
var sink = new SignalSink();

// Subscribe to see sub-operation signals
sink.Subscribe(signal =>
{
    if (signal.Signal.StartsWith("resize."))
    {
        var opId = signal.OperationId.HasValue ? $"(op:{signal.OperationId})" : "";
        Console.WriteLine($"{signal.Signal} {opId}");
    }
});

await using var pipeline = new ImagePipeline(sink)
    .WithLoader()
    .WithParallelResize(new ParallelResizeOptions
    {
        Sizes = new List<(Size, string)>
        {
            (new Size(100, 100), "tiny"),
            (new Size(200, 200), "small"),
            (new Size(400, 400), "medium"),
            (new Size(800, 800), "large"),
            (new Size(1920, 1920), "xlarge")
        },
        MaxParallelism = 3,  // Process 3 resizes concurrently
        JpegQuality = 90
    });

var result = await pipeline.ProcessAsync(job);

// Each resize gets its own operation ID
// Signals show: resize.tiny.started (op:123), resize.small.started (op:124), etc.
```

### Monitoring with Signals

```csharp
var sink = new SignalSink();

sink.Subscribe(signal =>
{
    if (signal.Signal.StartsWith("image."))
    {
        Console.WriteLine($"Image event: {signal.Signal}");
    }
    else if (signal.Signal.StartsWith("resize."))
    {
        Console.WriteLine($"Resize event: {signal.Signal}");
    }
});

await using var pipeline = new ImagePipeline(sink)
    .WithLoader()
    .WithResize();

await pipeline.ProcessAsync(job);
```

### Signal-Based Cancellation

Enable clean cancellation of image processing operations via operation-scoped signals:

```csharp
var sink = new SignalSink();

// Use within an EphemeralWorkCoordinator to get operation context
var coordinator = new EphemeralWorkCoordinator<ImageJob>(sink, async (job, op, ct) =>
{
    // Enable cancellation hook - listens for '{opid}.imagesharp.stop' signal
    await using var pipeline = new ImagePipeline(sink)
        .WithCancellationHook(op.Id, maxDimension: 5000)  // <-- Operation-scoped
        .WithLoader()
        .WithResize()
        .WithExif()
        .WithWatermark();

    return await pipeline.ProcessAsync(job, ct);
});

coordinator.Enqueue(new ImageJob("huge.jpg", "output", 0, 1));

// From another coordinator watching signals:
// When it sees image.dimensions:6000x4000 for operation 123
sink.Raise("imagesharp.stop", operationId: 123);  // Cancels ONLY operation 123
```

**Pattern 1: Explicit Stop Signal**
```csharp
// Another coordinator/watcher decides to stop this operation
sink.Raise("imagesharp.stop", operationId: 123);
```

**Pattern 2: Automatic Dimension-Based Cancellation**
```csharp
// Pipeline emits: image.dimensions:6000x4000 (opid: 123)
// Hook configured with maxDimension: 5000 sees this
// Automatically cancels because 6000 > 5000

await using var pipeline = new ImagePipeline(sink)
    .WithCancellationHook(op.Id, maxDimension: 5000)  // Auto-cancel large images
    .WithLoader()
    .WithResize();
```

**Signals emitted by cancellation:**
- `imagesharp.dimension.exceeded:{w}x{h}` - Image too large (opid-scoped)
- `imagesharp.stopping` - Cancellation initiated (opid-scoped)
- `imagesharp.stopped` - Cancellation token triggered (opid-scoped)
- `pipeline.cancelled:{n}` - Specific image cancelled

**How it works:**
1. `WithCancellationHook(opId, maxDim)` creates operation-scoped hook
2. Subscribes to signals WHERE `signal.OperationId == opId`
3. Listens for `imagesharp.stop` OR `image.dimensions:` exceeding max
4. When triggered, cancels the CancellationTokenSource
5. All async operations respect the token and cancel cleanly
6. Resources are properly disposed via finally blocks

**Example: Cross-Operation Coordination**
```csharp
// Supervisor coordinator watches all image operations
var supervisor = new SignalSink();
supervisor.Subscribe(signal =>
{
    // Watch for dimension signals
    if (signal.Signal.StartsWith("image.dimensions:"))
    {
        var dims = signal.Signal.Substring("image.dimensions:".Length);
        var parts = dims.Split('x');
        if (int.Parse(parts[0]) > 10000 || int.Parse(parts[1]) > 10000)
        {
            // Cancel this specific operation
            sink.Raise("imagesharp.stop", signal.OperationId);
            Console.WriteLine($"Cancelled operation {signal.OperationId} - image too large");
        }
    }
});
```

### Batch Processing

```csharp
var sink = new SignalSink();

await using var pipeline = new ImagePipeline(sink)
    .WithLoader()
    .WithResize()
    .WithExif()
    .WithWatermark();

var jobs = new List<ImageJob>();
for (int i = 0; i < 100; i++)
{
    jobs.Add(new ImageJob($"input{i}.jpg", "output", 0, i));
}

// Process with bounded concurrency
var semaphore = new SemaphoreSlim(4, 4);
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
});

var results = await Task.WhenAll(tasks);
```

## Architecture

### ImageProcessingContext
Flows through the pipeline, holding state and emitting signals.

**Properties:**
- `Job` - Original job specification
- `Image` - Loaded image (disposed automatically)
- `Outputs` - Dictionary of size name → output path
- `OriginalSize` - Input file size in bytes
- `Width`, `Height`, `Format` - Image metadata

**Methods:**
- `ToResult()` - Converts to `ImageProcessingResult`
- `DisposeAsync()` - Cleans up image resources

### ImagePipeline
Fluent builder for configuring processing pipeline.

**Builder Methods:**
- `WithLoader()` - Add load atom
- `WithResize(options?)` - Add resize atom
- `WithExif(options?)` - Add EXIF atom
- `WithWatermark(options?)` - Add watermark atom

**Execution:**
- `ProcessAsync(job, ct)` - Execute pipeline on job

## Signal Philosophy

All atoms follow the **Pure Notification** pattern:

- **Signal provides context** (what happened)
- **Atom holds state** (current truth)
- **Listeners query atoms** (get authoritative data)

Signals are notifications, not state carriers. The `ImageProcessingContext` is the source of truth.

## Dependencies

- `SixLabors.ImageSharp` - Core image processing
- `SixLabors.ImageSharp.Drawing` - Watermark text rendering
- `Mostlylucid.Ephemeral` - Signal infrastructure

## License

Unlicense - Public Domain

## Repository

https://github.com/scottgal/mostlylucid.atoms