namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Deterministic guard atom that enforces safety or compliance rules.
/// </summary>
/// <remarks>
///     Default contract: Kind = Guard, Determinism = Deterministic, Persistence = EphemeralOnly.
///     Output signals default to "atom.guard.output" unless overridden.
/// </remarks>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output payload type.</typeparam>
public sealed class GuardAtom<TInput, TOutput> : SignalDrivenAtom<TInput, TOutput>
{
    /// <summary>
    ///     Initializes a GuardAtom that emits to an untyped SignalSink.
    /// </summary>
    /// <param name="signals">Signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public GuardAtom(
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
    ///     Initializes a GuardAtom that emits to a TypedSignalSink.
    /// </summary>
    /// <param name="typedSignals">Typed signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public GuardAtom(
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        AtomContract? contract = null,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
        : base(
            contract ?? AtomContract.Create(AtomKind.Guard, AtomDeterminism.Deterministic,
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