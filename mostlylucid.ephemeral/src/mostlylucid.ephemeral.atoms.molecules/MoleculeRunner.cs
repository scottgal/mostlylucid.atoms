using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral.Atoms.Molecules;

/// <summary>
///     Listens for trigger signals and instantiates molecule blueprints.
/// </summary>
public sealed class MoleculeRunner : IAsyncDisposable
{
    private readonly IReadOnlyList<MoleculeBlueprint> _blueprints;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _running = new();
    private readonly IServiceProvider _services;
    private readonly SignalSink _signals;
    private bool _disposed;

    /// <summary>
    ///     Builds a runner.
    /// </summary>
    public MoleculeRunner(SignalSink signals, IEnumerable<MoleculeBlueprint> blueprints,
        IServiceProvider? services = null)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        if (blueprints is null) throw new ArgumentNullException(nameof(blueprints));
        _blueprints = blueprints.ToArray();
        if (_blueprints.Count == 0)
            throw new ArgumentException("At least one blueprint is required.", nameof(blueprints));
        _services = services ?? NullServiceProvider.Instance;
        _signals.SignalRaised += OnSignal;
    }

    /// <summary>
    ///     Cancel pending molecules and wait for running ones to complete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _signals.SignalRaised -= OnSignal;
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_running.ToArray()).ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>
    ///     Raised when a molecule begins execution.
    /// </summary>
    public event Action<MoleculeBlueprint, MoleculeContext>? MoleculeStarted;

    /// <summary>
    ///     Raised when a molecule completes.
    /// </summary>
    public event Action<MoleculeBlueprint, MoleculeContext>? MoleculeCompleted;

    /// <summary>
    ///     Raised when a molecule throws.
    /// </summary>
    public event Action<MoleculeBlueprint, MoleculeContext, Exception>? MoleculeFailed;

    private void OnSignal(SignalEvent signal)
    {
        if (_cts.IsCancellationRequested)
            return;

        foreach (var blueprint in _blueprints.Where(b => b.Matches(signal)))
        {
            var context = new MoleculeContext(signal, _signals, _services, _cts.Token);
            MoleculeStarted?.Invoke(blueprint, context);
            var running = Task.Run(() => ExecuteAsync(blueprint, context), _cts.Token);
            _running.Add(running);
        }
    }

    private async Task ExecuteAsync(MoleculeBlueprint blueprint, MoleculeContext context)
    {
        try
        {
            foreach (var step in blueprint.Steps)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await step(context, context.CancellationToken).ConfigureAwait(false);
            }

            MoleculeCompleted?.Invoke(blueprint, context);
        }
        catch (Exception ex)
        {
            MoleculeFailed?.Invoke(blueprint, context, ex);
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();

        private NullServiceProvider()
        {
        }

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}