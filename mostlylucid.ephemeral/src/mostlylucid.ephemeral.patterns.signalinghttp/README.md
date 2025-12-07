# Mostlylucid.Ephemeral.Patterns.SignalingHttp

HTTP client helper that emits fine-grained progress and stage signals during downloads.

## Signals Emitted

- `stage.starting` - Request starting
- `stage.request` - Request sent
- `stage.headers` - Headers received
- `stage.reading` - Reading body
- `stage.completed` - Download complete
- `progress:N` - Progress percentage (0-100)

## Usage

```csharp
var bytes = await SignalingHttpClient.DownloadWithSignalsAsync(
    httpClient,
    new HttpRequestMessage(HttpMethod.Get, url),
    signalEmitter,
    cancellationToken);
```

## License

MIT
