using System.Net;
using System.Net.Http;
using Mostlylucid.Helpers.Ephemeral;
using Mostlylucid.Helpers.Ephemeral.Examples;
using Xunit;

namespace Mostlylucid.Test.Ephemeral;

public class SignalingHttpClientExampleTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload = new byte[2048];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };
            response.Content.Headers.ContentLength = _payload.Length;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingEmitter : ISignalEmitter
    {
        private readonly List<string> _signals = new();
        public IReadOnlyList<string> Signals => _signals;
        public long OperationId => 1;
        public string? Key => "http-example";
        public void Emit(string signal) => _signals.Add(signal);
        public bool EmitCaused(string signal, SignalPropagation? cause) { _signals.Add(signal); return true; }
        public bool Retract(string signal) => false;
        public int RetractMatching(string pattern) => 0;
        public bool HasSignal(string signal) => _signals.Contains(signal);
    }

    [Fact]
    public async Task DownloadWithSignals_EmitsStagesAndProgress()
    {
        using var client = new HttpClient(new StubHandler());
        var emitter = new RecordingEmitter();

        var body = await SignalingHttpClient.DownloadWithSignalsAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/resource"),
            emitter);

        Assert.Equal(2048, body.Length);
        Assert.Equal("stage.starting", emitter.Signals[0]);
        Assert.Contains("stage.request", emitter.Signals);
        Assert.Contains("stage.headers", emitter.Signals);
        Assert.Contains("stage.reading", emitter.Signals);
        Assert.Contains("stage.completed", emitter.Signals);
        Assert.Contains(emitter.Signals, s => s == "progress:0");
        Assert.Contains(emitter.Signals, s => s == "progress:100");
    }
}
