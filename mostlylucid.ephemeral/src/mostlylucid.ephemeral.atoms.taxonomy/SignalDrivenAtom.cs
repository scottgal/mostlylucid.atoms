namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Base class for taxonomy atoms that execute a handler and emit typed signals on completion.
/// </summary>
/// <typeparam name="TInput">The input type accepted by the atom.</typeparam>
/// <typeparam name="TOutput">The output type produced by the atom.</typeparam>
public abstract class SignalDrivenAtom<TInput, TOutput> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<TInput> _coordinator;
    private readonly Func<TInput, CancellationToken, Task<TOutput>> _handler;
    private readonly Func<TInput, string?>? _keySelector;
    private readonly TypedSignalSink<TOutput> _typedSignals;
    private bool _disposed;

    /// <summary>
    ///     Initializes a signal-driven atom wrapper.
    /// </summary>
    /// <param name="contract">Execution contract for the atom.</param>
    /// <param name="typedSignals">Typed signal sink used to emit outputs.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="outputSignal">Optional override for the output signal name.</param>
    /// <param name="keySelector">Optional selector for per-input signal keys.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals entirely.</param>
    protected SignalDrivenAtom(
        AtomContract contract,
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _typedSignals = typedSignals ?? throw new ArgumentNullException(nameof(typedSignals));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _keySelector = keySelector;
        OutputSignal = emitOutputSignals
            ? string.IsNullOrWhiteSpace(outputSignal)
                ? DefaultOutputSignal(contract.Kind)
                : outputSignal
            : null;

        _coordinator = new EphemeralWorkCoordinator<TInput>(ProcessAsync, maxBodyDuration,
            options ?? new EphemeralOptions());
    }

    /// <summary>
    ///     Contract metadata for this atom.
    /// </summary>
    public AtomContract Contract { get; }

    /// <summary>
    ///     The signal name emitted on successful execution, or null if suppressed.
    /// </summary>
    public string? OutputSignal { get; }

    /// <summary>
    ///     Completes and drains the coordinator.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Enqueues input for coordinated execution.
    /// </summary>
    /// <param name="input">The input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask EnqueueAsync(TInput input, CancellationToken ct = default)
    {
        return _coordinator.EnqueueAsync(input, ct);
    }

    /// <summary>
    ///     Executes the handler immediately and returns the output.
    /// </summary>
    /// <param name="input">The input payload.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<TOutput> RunAsync(TInput input, CancellationToken ct = default)
    {
        return ExecuteAsync(input, ct);
    }

    /// <summary>
    ///     Drains outstanding queued work.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task DrainAsync(CancellationToken ct = default)
    {
        return _coordinator.DrainAsync(ct);
    }

    private async Task ProcessAsync(TInput input, CancellationToken ct)
    {
        await ExecuteAsync(input, ct).ConfigureAwait(false);
    }

    private async Task<TOutput> ExecuteAsync(TInput input, CancellationToken ct)
    {
        var output = await _handler(input, ct).ConfigureAwait(false);
        EmitOutput(output, input);
        return output;
    }

    private void EmitOutput(TOutput output, TInput input)
    {
        if (string.IsNullOrWhiteSpace(OutputSignal))
            return;

        var key = _keySelector?.Invoke(input);
        _typedSignals.Raise(OutputSignal!, output, key);
    }

    private static string DefaultOutputSignal(AtomKind kind)
    {
        return $"atom.{kind.Slug}.output";
    }
}