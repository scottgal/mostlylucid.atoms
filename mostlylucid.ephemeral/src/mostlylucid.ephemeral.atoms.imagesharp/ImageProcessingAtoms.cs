using Mostlylucid.Ephemeral;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;

namespace Mostlylucid.Ephemeral.Atoms.ImageSharp;

/// <summary>
/// Represents an image processing job with metadata
/// </summary>
public record ImageJob(string SourcePath, string OutputDir, int BatchNumber, int ImageNumber);

/// <summary>
/// Result of image processing with all output paths
/// </summary>
public record ImageProcessingResult(
    string OriginalPath,
    string ThumbnailPath,
    string MediumPath,
    string LargePath,
    string WatermarkedPath,
    TimeSpan ProcessingTime,
    long OriginalSize,
    long TotalOutputSize
);

/// <summary>
/// Image processing context that flows through the pipeline.
/// Holds state and emits signals as processing progresses.
/// </summary>
public sealed class ImageProcessingContext : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private Image? _image;
    private readonly Dictionary<string, string> _outputs = new();
    private readonly DateTime _startTime = DateTime.UtcNow;

    public ImageJob Job { get; }
    public Image Image => _image ?? throw new InvalidOperationException("Image not loaded");
    public IReadOnlyDictionary<string, string> Outputs => _outputs;
    public long OriginalSize { get; private set; }
    public string? FileName { get; private set; }
    public int Width => Image.Width;
    public int Height => Image.Height;
    public string Format => Image.Metadata.DecodedImageFormat?.Name ?? "Unknown";

    public ImageProcessingContext(SignalSink signals, ImageJob job)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }

    internal void SetImage(Image image)
    {
        _image = image;
    }

    internal void SetOriginalSize(long size)
    {
        OriginalSize = size;
    }

    internal void SetFileName(string name)
    {
        FileName = name;
    }

    internal void AddOutput(string sizeName, string path)
    {
        _outputs[sizeName] = path;
    }

    internal void Emit(string signal)
    {
        _signals.Raise(signal);
    }

    public ImageProcessingResult ToResult()
    {
        var processingTime = DateTime.UtcNow - _startTime;

        // Calculate total output size
        long totalSize = 0;
        foreach (var path in _outputs.Values)
        {
            if (File.Exists(path))
                totalSize += new FileInfo(path).Length;
        }

        return new ImageProcessingResult(
            Job.SourcePath,
            _outputs.GetValueOrDefault("thumb") ?? "",
            _outputs.GetValueOrDefault("medium") ?? "",
            _outputs.GetValueOrDefault("large") ?? "",
            _outputs.GetValueOrDefault("watermarked") ?? "",
            processingTime,
            OriginalSize,
            totalSize
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_image != null)
        {
            _image.Dispose();
            _image = null;
        }

        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// Load Image Atom - Loads images from disk with file I/O tracking.
/// Emits: image.loading, image.loaded, image.load.failed
/// </summary>
public sealed class LoadImageAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;

    public LoadImageAtom(SignalSink signals)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
    }

    /// <summary>
    /// Loads an image and returns a processing context.
    /// </summary>
    public async Task<ImageProcessingContext> LoadAsync(ImageJob job, CancellationToken ct = default)
    {
        var ctx = new ImageProcessingContext(_signals, job);

        try
        {
            ctx.Emit("image.loading");

            var fileInfo = new FileInfo(job.SourcePath);
            ctx.SetOriginalSize(fileInfo.Length);
            ctx.SetFileName(Path.GetFileName(job.SourcePath));

            var image = await Image.LoadAsync(job.SourcePath, ct);
            ctx.SetImage(image);

            ctx.Emit("image.loaded");
            _signals.Raise($"image.dimensions:{image.Width}x{image.Height}");
            _signals.Raise($"image.format:{ctx.Format}");

            return ctx;
        }
        catch (Exception ex)
        {
            ctx.Emit("image.load.failed");
            _signals.Raise($"image.error:{ex.Message}");
            await ctx.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Resize Atom - Creates multiple sized variants (thumbnail, medium, large).
/// Emits: resize.started, resize.{size}.started, resize.{size}.complete, resize.complete
/// </summary>
public sealed class ResizeImageAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly ResizeOptions _options;

    public ResizeImageAtom(SignalSink signals, ResizeOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _options = options ?? new ResizeOptions();
    }

    /// <summary>
    /// Resizes the image to multiple sizes defined in options.
    /// </summary>
    public async Task<ImageProcessingContext> ResizeAsync(
        ImageProcessingContext ctx,
        CancellationToken ct = default)
    {
        ctx.Emit("resize.started");
        _signals.Raise($"resize.count:{_options.Sizes.Count}");

        for (int i = 0; i < _options.Sizes.Count; i++)
        {
            var (size, sizeName) = _options.Sizes[i];

            ctx.Emit($"resize.{sizeName}.started");

            // Clone image for this size
            using var resized = ctx.Image.Clone(imgCtx =>
            {
                imgCtx.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Size = size,
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3
                });
            });

            // Generate output path
            var outputPath = Path.Combine(
                ctx.Job.OutputDir,
                $"batch{ctx.Job.BatchNumber:D3}",
                sizeName,
                $"img{ctx.Job.ImageNumber:D4}_{sizeName}.jpg"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Save with high quality JPEG
            await resized.SaveAsync(outputPath, new JpegEncoder { Quality = _options.JpegQuality }, ct);

            ctx.AddOutput(sizeName, outputPath);
            ctx.Emit($"resize.{sizeName}.complete");
            _signals.Raise($"file.saved:{outputPath}");
        }

        ctx.Emit("resize.complete");
        return ctx;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Configuration for ResizeImageAtom
/// </summary>
public sealed class ResizeOptions
{
    public List<(Size Size, string Name)> Sizes { get; init; } = new()
    {
        (new Size(150, 150), "thumb"),
        (new Size(800, 600), "medium"),
        (new Size(1920, 1080), "large")
    };

    public int JpegQuality { get; init; } = 90;
}

/// <summary>
/// EXIF Atom - Adds metadata to images (copyright, description, keywords).
/// Emits: exif.processing, exif.{size}.started, exif.{size}.complete, exif.complete
/// </summary>
public sealed class ExifProcessingAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly ExifOptions _options;

    public ExifProcessingAtom(SignalSink signals, ExifOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _options = options ?? new ExifOptions();
    }

    /// <summary>
    /// Adds EXIF metadata to all output images.
    /// </summary>
    public async Task<ImageProcessingContext> AddExifAsync(
        ImageProcessingContext ctx,
        CancellationToken ct = default)
    {
        ctx.Emit("exif.processing");

        // Add EXIF data to all output images
        foreach (var (sizeName, outputPath) in ctx.Outputs)
        {
            ctx.Emit($"exif.{sizeName}.started");

            using var img = await Image.LoadAsync(outputPath, ct);

            var exif = img.Metadata.ExifProfile ?? new ExifProfile();

            // Add metadata
            exif.SetValue(ExifTag.Copyright, _options.Copyright);
            exif.SetValue(ExifTag.ImageDescription,
                $"Generated image: Batch {ctx.Job.BatchNumber}, Image {ctx.Job.ImageNumber}, Size: {sizeName}");
            exif.SetValue(ExifTag.Software, _options.Software);
            exif.SetValue(ExifTag.Artist, _options.Artist);
            exif.SetValue(ExifTag.DateTime, DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss"));

            // Add custom keywords if provided (skip for now due to EXIF API complexity)
            // Keywords would require proper EXIF tag handling

            img.Metadata.ExifProfile = exif;

            await img.SaveAsync(outputPath, new JpegEncoder { Quality = 90 }, ct);

            ctx.Emit($"exif.{sizeName}.complete");
            _signals.Raise($"exif.written:{sizeName}");
        }

        ctx.Emit("exif.complete");
        return ctx;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Configuration for ExifProcessingAtom
/// </summary>
public sealed class ExifOptions
{
    public string Copyright { get; init; } = "© 2025 Mostlylucid.Ephemeral Benchmark";
    public string Software { get; init; } = "Mostlylucid.Ephemeral ImageProcessing Demo";
    public string Artist { get; init; } = "EphemeralWorkCoordinator";
    public List<string> Keywords { get; init; } = new();
}

/// <summary>
/// Watermark Atom - Adds text watermark to large images.
/// Emits: watermark.started, watermark.rendering, watermark.complete, processing.complete
/// </summary>
public sealed class WatermarkAtom : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly WatermarkOptions _options;

    public WatermarkAtom(SignalSink signals, WatermarkOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _options = options ?? new WatermarkOptions();
    }

    /// <summary>
    /// Adds watermark to the large image variant.
    /// </summary>
    public async Task<ImageProcessingContext> AddWatermarkAsync(
        ImageProcessingContext ctx,
        CancellationToken ct = default)
    {
        ctx.Emit("watermark.started");

        // Add watermark to large image
        if (ctx.Outputs.TryGetValue(_options.TargetSize, out var largePath))
        {
            ctx.Emit("watermark.rendering");

            using var img = await Image.LoadAsync(largePath, ct);

            img.Mutate(imgCtx =>
            {
                // Calculate watermark position
                var x = _options.HorizontalAlignment switch
                {
                    HorizontalAlignment.Left => _options.Padding,
                    HorizontalAlignment.Right => img.Width - _options.Padding - _options.EstimatedWidth,
                    _ => (img.Width / 2f) - (_options.EstimatedWidth / 2f)
                };

                var y = _options.VerticalAlignment switch
                {
                    VerticalAlignment.Top => _options.Padding,
                    VerticalAlignment.Bottom => img.Height - _options.Padding - _options.FontSize,
                    _ => (img.Height / 2f) - (_options.FontSize / 2f)
                };

                // Add semi-transparent watermark text
                imgCtx.DrawText(
                    _options.Text,
                    SystemFonts.CreateFont(_options.FontFamily, _options.FontSize),
                    Color.FromRgba(
                        _options.Color.R,
                        _options.Color.G,
                        _options.Color.B,
                        _options.Opacity),
                    new PointF(x, y)
                );
            });

            var watermarkedPath = Path.Combine(
                Path.GetDirectoryName(largePath)!,
                $"img{ctx.Job.ImageNumber:D4}_watermarked.jpg"
            );

            await img.SaveAsync(watermarkedPath, new JpegEncoder { Quality = _options.Quality }, ct);
            ctx.AddOutput("watermarked", watermarkedPath);

            ctx.Emit("watermark.complete");
            _signals.Raise($"watermark.applied:{watermarkedPath}");
        }

        ctx.Emit("processing.complete");
        _signals.Raise($"image.pipeline.complete:{ctx.Job.ImageNumber}");

        return ctx;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Configuration for WatermarkAtom
/// </summary>
public sealed class WatermarkOptions
{
    public string Text { get; init; } = "Ephemeral Processing";
    public string FontFamily { get; init; } = "Arial";
    public float FontSize { get; init; } = 48;
    public (byte R, byte G, byte B) Color { get; init; } = (255, 255, 255);
    public byte Opacity { get; init; } = 128;
    public int Quality { get; init; } = 95;
    public string TargetSize { get; init; } = "large";
    public HorizontalAlignment HorizontalAlignment { get; init; } = HorizontalAlignment.Center;
    public VerticalAlignment VerticalAlignment { get; init; } = VerticalAlignment.Bottom;
    public float Padding { get; init; } = 80f;
    public float EstimatedWidth { get; init; } = 400f;
}

public enum HorizontalAlignment { Left, Center, Right }
public enum VerticalAlignment { Top, Center, Bottom }

/// <summary>
/// Cancellation hook that listens for operation-scoped signals and cleanly cancels operations.
/// Listens for both dimension-based signals (image.dimensions:5000x5000) and explicit stop signals.
/// </summary>
public sealed class ImageSharpCancellationHook : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly CancellationTokenSource _cts;
    private readonly IDisposable _subscription;
    private readonly long _operationId;
    private bool _disposed;

    /// <summary>
    /// Maximum allowed width or height. Images exceeding this will be cancelled.
    /// </summary>
    public int? MaxDimension { get; init; }

    public ImageSharpCancellationHook(SignalSink signals, long operationId, int? maxDimension = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _operationId = operationId;
        _cts = new CancellationTokenSource();
        MaxDimension = maxDimension;

        // Subscribe to operation-scoped signals
        _subscription = _signals.Subscribe(signal =>
        {
            if (_disposed || signal.OperationId != _operationId)
                return;

            // Pattern 1: Explicit stop signal - {opid}.imagesharp.stop
            if (signal.Signal == "imagesharp.stop")
            {
                _signals.Raise(new SignalEvent("imagesharp.stopping", _operationId, null, DateTimeOffset.UtcNow));
                _cts.Cancel();
                _signals.Raise(new SignalEvent("imagesharp.stopped", _operationId, null, DateTimeOffset.UtcNow));
                return;
            }

            // Pattern 2: Dimension check - another coordinator sees image.dimensions:{w}x{h}
            if (MaxDimension.HasValue && signal.Signal.StartsWith("image.dimensions:"))
            {
                var dimensions = signal.Signal.Substring("image.dimensions:".Length);
                var parts = dimensions.Split('x');

                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var width) &&
                    int.TryParse(parts[1], out var height))
                {
                    if (width > MaxDimension.Value || height > MaxDimension.Value)
                    {
                        _signals.Raise(new SignalEvent($"imagesharp.dimension.exceeded:{width}x{height}", _operationId, null, DateTimeOffset.UtcNow));
                        _signals.Raise(new SignalEvent("imagesharp.stopping", _operationId, null, DateTimeOffset.UtcNow));
                        _cts.Cancel();
                        _signals.Raise(new SignalEvent("imagesharp.stopped", _operationId, null, DateTimeOffset.UtcNow));
                    }
                }
            }
        });
    }

    /// <summary>
    /// Cancellation token that will be cancelled when stop signal is raised or dimensions exceeded.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Manually triggers cancellation for this operation.
    /// </summary>
    public void Cancel()
    {
        if (!_disposed)
        {
            _signals.Raise(new SignalEvent("imagesharp.stopping", _operationId, null, DateTimeOffset.UtcNow));
            _cts.Cancel();
            _signals.Raise(new SignalEvent("imagesharp.stopped", _operationId, null, DateTimeOffset.UtcNow));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _subscription.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Fluent builder for image processing pipelines.
/// Provides an ImageSharp-like API with Ephemeral signals.
/// </summary>
public sealed class ImagePipeline : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private LoadImageAtom? _loader;
    private ResizeImageAtom? _resizer;
    private ExifProcessingAtom? _exifProcessor;
    private WatermarkAtom? _watermarker;
    private ImageSharpCancellationHook? _cancellationHook;

    public ImagePipeline(SignalSink signals)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
    }

    /// <summary>
    /// Enables signal-based cancellation for a specific operation.
    /// When '{opid}.imagesharp.stop' signal is raised, the operation will be cleanly cancelled.
    /// Optionally enforces maximum dimension - cancels if image exceeds width or height.
    /// </summary>
    /// <param name="operationId">The operation ID to scope signals to</param>
    /// <param name="maxDimension">Optional maximum width or height (e.g., 5000 to prevent 5000x5000+ images)</param>
    public ImagePipeline WithCancellationHook(long operationId, int? maxDimension = null)
    {
        _cancellationHook = new ImageSharpCancellationHook(_signals, operationId, maxDimension);
        return this;
    }

    /// <summary>
    /// Configures the loader atom.
    /// </summary>
    public ImagePipeline WithLoader()
    {
        _loader = new LoadImageAtom(_signals);
        return this;
    }

    /// <summary>
    /// Configures the resizer atom with custom options.
    /// </summary>
    public ImagePipeline WithResize(ResizeOptions? options = null)
    {
        _resizer = new ResizeImageAtom(_signals, options);
        return this;
    }

    /// <summary>
    /// Configures the EXIF processor atom with custom options.
    /// </summary>
    public ImagePipeline WithExif(ExifOptions? options = null)
    {
        _exifProcessor = new ExifProcessingAtom(_signals, options);
        return this;
    }

    /// <summary>
    /// Configures the watermark atom with custom options.
    /// </summary>
    public ImagePipeline WithWatermark(WatermarkOptions? options = null)
    {
        _watermarker = new WatermarkAtom(_signals, options);
        return this;
    }

    /// <summary>
    /// Executes the configured pipeline on a single job.
    /// If cancellation hook is enabled, operations can be cancelled via imagesharp.stop signal.
    /// </summary>
    public async Task<ImageProcessingResult> ProcessAsync(
        ImageJob job,
        CancellationToken ct = default)
    {
        if (_loader == null)
            throw new InvalidOperationException("Pipeline must have a loader. Call WithLoader().");

        // Combine external cancellation token with hook token (if enabled)
        using var linkedCts = _cancellationHook != null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellationHook.Token)
            : null;

        var effectiveToken = linkedCts?.Token ?? ct;

        _signals.Raise($"pipeline.started:{job.ImageNumber}");

        ImageProcessingContext? ctx = null;
        try
        {
            // Load
            ctx = await _loader.LoadAsync(job, effectiveToken);

            // Resize (if configured)
            if (_resizer != null)
            {
                ctx = await _resizer.ResizeAsync(ctx, effectiveToken);
            }

            // EXIF (if configured)
            if (_exifProcessor != null)
            {
                ctx = await _exifProcessor.AddExifAsync(ctx, effectiveToken);
            }

            // Watermark (if configured)
            if (_watermarker != null)
            {
                ctx = await _watermarker.AddWatermarkAsync(ctx, effectiveToken);
            }

            var result = ctx.ToResult();
            _signals.Raise($"pipeline.complete:{job.ImageNumber}");

            return result;
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            _signals.Raise($"pipeline.cancelled:{job.ImageNumber}");
            throw;
        }
        finally
        {
            if (ctx != null)
            {
                await ctx.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loader != null) await _loader.DisposeAsync();
        if (_resizer != null) await _resizer.DisposeAsync();
        if (_exifProcessor != null) await _exifProcessor.DisposeAsync();
        if (_watermarker != null) await _watermarker.DisposeAsync();
        if (_cancellationHook != null) await _cancellationHook.DisposeAsync();
    }
}
