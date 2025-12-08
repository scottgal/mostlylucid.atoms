namespace Mostlylucid.Ephemeral.Patterns.SignalingHttp;

/// <summary>
///     Example helper showing fine-grained signal emission during HTTP calls.
///     Emits stage markers and progress signals (progress:xx) without blocking the caller.
/// </summary>
public static class SignalingHttpClient
{
    /// <summary>
    ///     Download a response while emitting rich, low-overhead signals.
    /// </summary>
    public static async Task<byte[]> DownloadWithSignalsAsync(
        HttpClient client,
        HttpRequestMessage request,
        ISignalEmitter emitter,
        CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (emitter is null) throw new ArgumentNullException(nameof(emitter));

        emitter.Emit("stage.starting");
        emitter.Emit("progress:0");
        emitter.Emit("stage.request");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        emitter.Emit("stage.headers");

        var contentLength = response.Content.Headers.ContentLength;
        emitter.Emit("stage.reading");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var totalRead = 0L;
        var payload = new byte[8 * 1024];

        int read;
        while ((read = await stream.ReadAsync(payload.AsMemory(0, payload.Length), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(payload, 0, read);
            totalRead += read;

            if (contentLength is > 0)
            {
                var percent = (int)Math.Min(100, totalRead * 100 / contentLength.Value);
                emitter.Emit($"progress:{percent}");
            }
        }

        if (contentLength is null) emitter.Emit("progress:100");

        emitter.Emit("stage.completed");

        return buffer.ToArray();
    }
}