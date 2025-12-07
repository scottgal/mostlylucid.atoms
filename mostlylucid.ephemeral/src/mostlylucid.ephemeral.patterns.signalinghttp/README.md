# Mostlylucid.Ephemeral.Patterns.SignalingHttp

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.signalinghttp.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.signalinghttp)

HTTP client helper that emits fine-grained progress and stage signals during downloads.

```bash
dotnet add package mostlylucid.ephemeral.patterns.signalinghttp
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.SignalingHttp;

var bytes = await SignalingHttpClient.DownloadWithSignalsAsync(
    httpClient,
    new HttpRequestMessage(HttpMethod.Get, url),
    signalEmitter,
    cancellationToken);
```

---

## API Reference

```csharp
// Download with signal emission
Task<byte[]> SignalingHttpClient.DownloadWithSignalsAsync(
    // Required: HttpClient instance
    HttpClient client,

    // Required: HTTP request message
    HttpRequestMessage request,

    // Required: signal emitter (from operation or sink)
    ISignalEmitter emitter,

    // Optional: cancellation token
    CancellationToken ct = default
)
```

---

## Signals Emitted

| Signal | Description |
|--------|-------------|
| `stage.starting` | Request starting |
| `stage.request` | Request sent |
| `stage.headers` | Headers received |
| `stage.reading` | Reading body |
| `stage.completed` | Download complete |
| `progress:N` | Progress percentage (0-100) |

---

## How It Works

```
[start] ─> stage.starting ─> progress:0 ─> stage.request
                                              │
                                              ▼
                               [send request to server]
                                              │
                                              ▼
                                        stage.headers
                                              │
                                              ▼
                                        stage.reading
                                              │
    ┌─────────────────────────────────────────┴─────────────────────────────────────────┐
    │                                                                                     │
progress:10 ─> progress:25 ─> progress:50 ─> progress:75 ─> progress:100 ─> stage.completed
```

---

## Example: Track Download Progress

```csharp
await using var coordinator = new EphemeralWorkCoordinator<DownloadRequest>(
    async (req, ct) =>
    {
        var emitter = coordinator.GetEmitter(req.OperationId);
        var bytes = await SignalingHttpClient.DownloadWithSignalsAsync(
            httpClient,
            new HttpRequestMessage(HttpMethod.Get, req.Url),
            emitter,
            ct);

        await File.WriteAllBytesAsync(req.OutputPath, bytes, ct);
    });

// Monitor progress
var progressSignals = coordinator.GetSignalsByPattern("progress:*");
foreach (var signal in progressSignals)
{
    var percent = signal.Signal.Split(':')[1];
    Console.WriteLine($"Download progress: {percent}%");
}
```

---

## Example: With Signal Sink

```csharp
var sink = new SignalSink();

// Create a simple emitter from the sink
var emitter = new SinkEmitter(sink, operationId: 1);

var bytes = await SignalingHttpClient.DownloadWithSignalsAsync(
    httpClient,
    new HttpRequestMessage(HttpMethod.Get, "https://example.com/file.zip"),
    emitter);

// Query all stage signals
var stages = sink.Sense(s => s.Signal.StartsWith("stage."));
foreach (var stage in stages.OrderBy(s => s.Timestamp))
    Console.WriteLine($"{stage.Timestamp}: {stage.Signal}");
```

---

## Example: Multiple Concurrent Downloads

```csharp
await using var coordinator = new EphemeralWorkCoordinator<string>(
    async (url, ct) =>
    {
        var id = await coordinator.EnqueueWithIdAsync(url);
        var emitter = coordinator.GetEmitter(id);

        await SignalingHttpClient.DownloadWithSignalsAsync(
            httpClient,
            new HttpRequestMessage(HttpMethod.Get, url),
            emitter,
            ct);
    },
    new EphemeralOptions { MaxConcurrency = 4 });

// Enqueue multiple downloads
foreach (var url in urls)
    await coordinator.EnqueueAsync(url);

// Watch for completions
var completed = coordinator.GetSignalsByPattern("stage.completed");
Console.WriteLine($"Completed: {completed.Count} downloads");
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.telemetry](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.telemetry) | Telemetry integration |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
