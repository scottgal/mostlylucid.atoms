namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Probabilistic proposer atom that emits proposals with confidence.
/// </summary>
/// <remarks>
///     Default contract: Kind = Proposer, Determinism = Probabilistic, Persistence = PersistableViaEscalation.
///     Output signals default to "atom.proposer.output" unless overridden.
/// </remarks>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output payload type.</typeparam>
public sealed class ProposerAtom<TInput, TOutput> : SignalDrivenAtom<TInput, TOutput>
{
    /// <summary>
    ///     Initializes a ProposerAtom that emits to an untyped SignalSink.
    /// </summary>
    /// <param name="signals">Signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public ProposerAtom(
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
    ///     Initializes a ProposerAtom that emits to a TypedSignalSink.
    /// </summary>
    /// <param name="typedSignals">Typed signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public ProposerAtom(
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        AtomContract? contract = null,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
        : base(
            contract ?? AtomContract.Create(AtomKind.Proposer, AtomDeterminism.Probabilistic,
                AtomPersistence.PersistableViaEscalation),
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