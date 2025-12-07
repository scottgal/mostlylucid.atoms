using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
/// Listens for signals and enqueues attributed jobs on a background coordinator.
/// </summary>
public sealed class EphemeralSignalJobRunner : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly EphemeralWorkCoordinator<EphemeralJobInvocation> _coordinator;
    private readonly IReadOnlyList<EphemeralJobDescriptor> _jobs;
    private readonly CancellationTokenSource _cts = new();

    public EphemeralSignalJobRunner(SignalSink signals, IEnumerable<object> jobTargets, EphemeralOptions? options = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        if (jobTargets is null) throw new ArgumentNullException(nameof(jobTargets));

        _jobs = jobTargets.SelectMany(EphemeralJobScanner.Scan).ToList();
        if (_jobs.Count == 0)
            throw new ArgumentException("No attributed jobs were discovered.", nameof(jobTargets));

        var opts = options ?? new EphemeralOptions
        {
            Signals = _signals,
            MaxConcurrency = Math.Max(1, _jobs.Count)
        };

        _coordinator = new EphemeralWorkCoordinator<EphemeralJobInvocation>(async (work, ct) =>
        {
            await work.Descriptor.InvokeAsync(ct, work.Signal).ConfigureAwait(false);
        }, opts);

        _signals.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent signal)
    {
        foreach (var job in _jobs)
        {
            if (!job.Matches(signal))
                continue;

            _ = _coordinator.EnqueueAsync(new EphemeralJobInvocation(job, signal), _cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _signals.SignalRaised -= OnSignal;
        _cts.Cancel();
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed record EphemeralJobInvocation(EphemeralJobDescriptor Descriptor, SignalEvent Signal);
}
