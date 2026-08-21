namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Deterministic coordinator atom that plans and orchestrates work.
/// </summary>
/// <remarks>
///     Default contract: Kind = Coordinator, Determinism = Deterministic, Persistence = EphemeralOnly.
///     Output signals default to "atom.coordinator.output" unless overridden.
/// </remarks>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output payload type.</typeparam>
public sealed class CoordinatorAtom<TInput, TOutput> : SignalDrivenAtom<TInput, TOutput>
{
    /// <summary>
    ///     Initializes a CoordinatorAtom that emits to an untyped SignalSink.
    /// </summary>
    /// <param name="signals">Signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public CoordinatorAtom(
        SignalSink signals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        AtomContract? contract = null,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
        : this(new TypedSignalSink<TOutput>(signals), handler, maxBodyDuration, contract, outputSignal, keySelector, options,
            emitOutputSignals)
    {
    }

    /// <summary>
    ///     Initializes a CoordinatorAtom that emits to a TypedSignalSink.
    /// </summary>
    /// <param name="typedSignals">Typed signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public CoordinatorAtom(
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        AtomContract? contract = null,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
        : base(
            contract ?? AtomContract.Create(AtomKind.Coordinator, AtomDeterminism.Deterministic,
                AtomPersistence.EphemeralOnly),
            typedSignals,
            handler,
            maxBodyDuration,
            outputSignal,
            keySelector,
            options,
            emitOutputSignals)
    {
    }
}