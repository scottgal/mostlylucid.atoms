# GCRA Rate Limiting

Generic Cell Rate Algorithm (GCRA) rate limiting for Ephemeral, with full signal integration.

## What is GCRA?

GCRA is a **leaky bucket variant** that provides smooth, evenly-distributed rate limiting without periodic token refills. Instead of adding tokens at intervals, GCRA calculates a "theoretical arrival time" (TAT) for each request.

### How It Works

GCRA tracks a single timestamp called the **Theoretical Arrival Time (TAT)**:

1. Each request increments TAT by the **emission interval** (1/rate)
2. TAT represents when the "virtual bucket" will be empty
3. Requests are allowed if: `now >= TAT - burst_capacity`
4. This creates smooth rate limiting without periodic updates

### Example

With `rate=10/s` and `burst=5`:

- **Emission interval**: 100ms (1 second / 10 requests)
- **Burst capacity**: 400ms (4 × 100ms, since burst-1)
- If TAT is at time `T`:
  - Request at `T-300ms`: ✅ ALLOWED (within burst capacity)
  - Request at `T+50ms`: ❌ DENIED (would exceed rate)

## Why GCRA?

| Algorithm | Behavior | Best For |
|-----------|----------|----------|
| **Token Bucket** | Periodic refills, allows bursts | Bursty workloads with known intervals |
| **GCRA** | Smooth distribution, no periodic timers | Evenly-spread requests, low jitter |

**GCRA advantages:**
- ✅ No background timers or periodic updates
- ✅ Smoother rate limiting (less "sawtooth" pattern)
- ✅ More predictable latency
- ✅ Simpler state (just one timestamp)

Inspired by: [github.com/boinkor-net/governor](https://github.com/boinkor-net/governor)

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.ratelimit
```

## Quick Start

### Real-World Example: Image Processing Service

Here's a practical example of using GCRA to rate limit an image processing service that calls an external API:

```csharp
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.RateLimit;
using Mostlylucid.Ephemeral.Atoms.ImageSharp;

public class ImageProcessingService
{
    private readonly SignalSink _signals;
    private readonly GcraRateLimitAtom _apiRateLimiter;
    private readonly HttpClient _httpClient;

    public ImageProcessingService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _signals = new SignalSink();

        // External API allows 50 requests/second with burst of 10
        _apiRateLimiter = new GcraRateLimitAtom(_signals, new GcraRateLimitOptions
        {
            InitialRatePerSecond = 50,
            InitialBurstSize = 10,
            EmitSignals = true
        });

        // React to API errors by slowing down
        _signals.Subscribe(signal =>
        {
            if (signal.Signal == "api.error.429")
            {
                // Got rate limited - reduce rate by 50%
                var newRate = _apiRateLimiter.RatePerSecond * 0.5;
                _signals.Raise($"rate.limit.gcra.set:{newRate}");
                Console.WriteLine($"⚠️  Rate limited by API - reducing to {newRate:F0}/s");
            }
            else if (signal.Signal == "api.success" && _apiRateLimiter.RatePerSecond < 50)
            {
                // Gradually recover rate
                var newRate = Math.Min(_apiRateLimiter.RatePerSecond * 1.05, 50);
                _signals.Raise($"rate.limit.gcra.set:{newRate}");
            }
        });
    }

    public async Task ProcessImagesAsync(IEnumerable<string> imagePaths, CancellationToken ct)
    {
        var sink = _signals;

        // Process images with bounded concurrency and rate limiting
        await using var coordinator = new EphemeralWorkCoordinator<string>(
            async (imagePath, opCt) =>
            {
                // Wait for rate limit before calling external API
                await _apiRateLimiter.AcquireAsync(opCt);

                try
                {
                    // Call external image optimization API
                    using var imageData = File.OpenRead(imagePath);
                    var content = new StreamContent(imageData);
                    var response = await _httpClient.PostAsync("/api/optimize", content, opCt);

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        sink.Raise("api.error.429");
                        throw new HttpRequestException("Rate limited by API");
                    }

                    response.EnsureSuccessStatusCode();
                    sink.Raise("api.success");

                    // Save optimized image
                    var optimized = await response.Content.ReadAsByteArrayAsync(opCt);
                    var outputPath = imagePath.Replace(".jpg", "_optimized.jpg");
                    await File.WriteAllBytesAsync(outputPath, optimized, opCt);

                    sink.Raise($"image.processed:{Path.GetFileName(imagePath)}");
                }
                catch (Exception ex)
                {
                    sink.Raise($"image.failed:{Path.GetFileName(imagePath)}");
                    Console.WriteLine($"❌ Failed: {imagePath} - {ex.Message}");
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 5,  // Process 5 images concurrently
                Signals = sink
            });

        // Enqueue all images
        foreach (var path in imagePaths)
        {
            coordinator.Enqueue(path);
        }

        // Wait for completion
        await Task.Delay(100);  // Give time for work to start
        while (coordinator.Stats.ActiveCount > 0 || coordinator.Stats.PendingCount > 0)
        {
            var stats = coordinator.Stats;
            Console.WriteLine($"📊 Active: {stats.ActiveCount}, Pending: {stats.PendingCount}, " +
                            $"Rate: {_apiRateLimiter.RatePerSecond:F0}/s");
            await Task.Delay(1000, ct);
        }

        // Summary
        var processed = sink.Sense(s => s.Signal.StartsWith("image.processed")).Count;
        var failed = sink.Sense(s => s.Signal.StartsWith("image.failed")).Count;
        var rateLimited = sink.Sense(s => s.Signal == "api.error.429").Count;
        var delayed = sink.Sense(s => s.Signal.StartsWith("rate.limit.gcra.delayed")).Count;

        Console.WriteLine($"\n✅ Complete: {processed} processed, {failed} failed");
        Console.WriteLine($"📉 Rate limiting: {rateLimited} API rejections, {delayed} delayed requests");
        Console.WriteLine($"🎯 Final rate: {_apiRateLimiter.RatePerSecond:F0}/s");
    }
}

// Usage
var service = new ImageProcessingService(httpClient);
var images = Directory.GetFiles("./input", "*.jpg");
await service.ProcessImagesAsync(images, cancellationToken);
```

**What this does:**

1. **Smooth Rate Limiting**: GCRA ensures API calls are evenly distributed at 50/second
2. **Burst Handling**: First 10 requests can go immediately (burst capacity)
3. **Auto-Recovery**: If API returns 429 (rate limit), automatically reduces rate by 50%
4. **Gradual Increase**: Slowly increases rate back to 50/s as requests succeed
5. **Signal-Based Observability**: All events (delays, API errors, successes) emit signals
6. **Coordinated Concurrency**: 5 concurrent workers, each rate-limited individually

### Basic Usage (Simple Case)

```csharp
using Mostlylucid.Ephemeral.Atoms.RateLimit;

// Create a GCRA limiter: 10 requests/second, burst of 5
var limiter = new GcraRateLimiter(ratePerSecond: 10, burstSize: 5);

// Try to acquire without waiting
if (limiter.TryAcquire())
{
    // Request allowed - proceed
    await ProcessRequestAsync();
}
else
{
    // Rate limited - reject or queue
    return StatusCode(429, "Too Many Requests");
}

// Or wait for permission
await limiter.AcquireAsync(cancellationToken);
await ProcessRequestAsync();
```

### Signal-Integrated Atom

```csharp
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.RateLimit;

var sink = new SignalSink();

// Create signal-driven rate limiter
await using var rateLimiter = new GcraRateLimitAtom(sink, new GcraRateLimitOptions
{
    InitialRatePerSecond = 100,
    InitialBurstSize = 10,
    EmitSignals = true  // Emit signals on allow/deny/delay
});

// Process requests with rate limiting
foreach (var request in requests)
{
    await rateLimiter.AcquireAsync(ct);
    await ProcessAsync(request);
}

// Query rate limit events
var denials = sink.Sense(s => s.Signal == "rate.limit.gcra.denied");
var delays = sink.Sense(s => s.Signal.StartsWith("rate.limit.gcra.delayed"));
Console.WriteLine($"Denied: {denials.Count}, Delayed: {delays.Count}");
```

## Dynamic Rate Adjustment

GCRA atoms respond to control signals for runtime adjustment:

```csharp
var sink = new SignalSink();
await using var limiter = new GcraRateLimitAtom(sink);

// Increase rate via signal
sink.Raise("rate.limit.gcra.set:200");  // 200 requests/second

// Adjust burst
sink.Raise("rate.limit.gcra.burst:20");  // Burst of 20

// Reset accumulated delay
sink.Raise("rate.limit.gcra.reset");
```

### Adaptive Rate Limiting

Adjust rates based on downstream signals:

```csharp
var sink = new SignalSink();
await using var limiter = new GcraRateLimitAtom(sink, new GcraRateLimitOptions
{
    InitialRatePerSecond = 100
});

// Listen for backpressure signals
sink.Subscribe(signal =>
{
    if (signal.Signal == "downstream.overload")
    {
        // Reduce rate by 50%
        var currentRate = limiter.RatePerSecond;
        sink.Raise($"rate.limit.gcra.set:{currentRate * 0.5}");
    }
    else if (signal.Signal == "downstream.healthy")
    {
        // Gradually increase rate
        var currentRate = limiter.RatePerSecond;
        sink.Raise($"rate.limit.gcra.set:{Math.Min(currentRate * 1.1, 1000)}");
    }
});
```

## Emitted Signals

When `EmitSignals = true`, the atom emits:

| Signal | When | Payload |
|--------|------|---------|
| `rate.limit.gcra.allowed` | Request immediately allowed | - |
| `rate.limit.gcra.delayed:{ms}` | Request delayed | Delay in milliseconds |
| `rate.limit.gcra.denied` | Request denied (TryAcquire) | - |
| `rate.limit.gcra.config:{rate},{burst}` | Configuration changed | Current rate and burst |
| `rate.limit.gcra.reset` | State reset | - |

## Control Signals

Send these signals to control the limiter:

| Signal | Effect | Example |
|--------|--------|---------|
| `rate.limit.gcra.set:{rate}` | Set rate per second | `rate.limit.gcra.set:50` |
| `rate.limit.gcra.burst:{size}` | Set burst size | `rate.limit.gcra.burst:10` |
| `rate.limit.gcra.reset` | Reset state | `rate.limit.gcra.reset` |

## Configuration Options

```csharp
public sealed class GcraRateLimitOptions
{
    /// Initial rate limit (requests per second). Default: 10.
    public double InitialRatePerSecond { get; init; } = 10;

    /// Initial burst size. Default: 5.
    public int InitialBurstSize { get; init; } = 5;

    /// Pattern for control signals. Default: "rate.limit.gcra.*"
    public string ControlSignalPattern { get; init; } = "rate.limit.gcra.*";

    /// Whether to emit signals. Default: true.
    public bool EmitSignals { get; init; } = true;
}
```

## Common Patterns

### Per-User Rate Limiting

```csharp
var sink = new SignalSink();
var limiters = new ConcurrentDictionary<string, GcraRateLimitAtom>();

async Task<bool> CheckRateLimit(string userId)
{
    var limiter = limiters.GetOrAdd(userId, _ =>
        new GcraRateLimitAtom(sink, new GcraRateLimitOptions
        {
            InitialRatePerSecond = 10,
            InitialBurstSize = 5
        }));

    return limiter.TryAcquire();
}
```

### Circuit Breaker Integration

```csharp
var sink = new SignalSink();
await using var limiter = new GcraRateLimitAtom(sink);

sink.Subscribe(signal =>
{
    // Stop all requests when circuit opens
    if (signal.Signal == "circuit.open")
    {
        sink.Raise("rate.limit.gcra.set:0");  // Zero requests allowed
    }
    else if (signal.Signal == "circuit.closed")
    {
        sink.Raise("rate.limit.gcra.set:100");  // Restore normal rate
    }
});
```

### Coordinated Rate Limiting

Use with `EphemeralWorkCoordinator` for bounded async work:

```csharp
var sink = new SignalSink();
await using var limiter = new GcraRateLimitAtom(sink, new GcraRateLimitOptions
{
    InitialRatePerSecond = 50
});

await using var coordinator = new EphemeralWorkCoordinator<ApiRequest>(
    async (request, ct) =>
    {
        // Acquire rate limit before processing
        await limiter.AcquireAsync(ct);

        // Process request
        var response = await httpClient.GetAsync(request.Url, ct);
        sink.Raise(response.IsSuccessStatusCode ? "api.success" : "api.failure");
    },
    new EphemeralOptions
    {
        MaxConcurrency = 10,
        Signals = sink
    });

// Enqueue work
foreach (var request in requests)
{
    coordinator.Enqueue(request);
}
```

## Performance

GCRA has minimal overhead:

- **Lock contention**: Single lock per acquire (only during TAT calculation)
- **Memory**: ~16 bytes of state (one timestamp)
- **CPU**: Simple arithmetic (no timers, no background threads)
- **Allocations**: Zero (except for signal emissions if enabled)

## References

- [Generic Cell Rate Algorithm (Wikipedia)](https://en.wikipedia.org/wiki/Generic_cell_rate_algorithm)
- [Governor Library (Rust)](https://github.com/boinkor-net/governor) - Inspiration for this implementation
- [Cloudflare: How we built rate limiting](https://blog.cloudflare.com/counting-things-a-lot-of-different-things/)

## See Also

- `RateLimitAtom` - Token bucket-based rate limiting
- `EphemeralWorkCoordinator` - Bounded async work coordination
- [Signal Pattern Documentation](../../README.md#signals)
