namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Deterministic extractor atom that turns raw content into stable semantic units.
/// </summary>
/// <remarks>
///     Default contract: Kind = Extractor, Determinism = Deterministic, Persistence = PersistableViaEscalation.
///     Output signals default to "atom.extractor.output" unless overridden.
/// </remarks>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output payload type.</typeparam>
public sealed class ExtractorAtom<TInput, TOutput> : SignalDrivenAtom<TInput, TOutput>
{
    /// <summary>
    ///     Initializes an ExtractorAtom that emits to an untyped SignalSink.
    /// </summary>
    /// <param name="signals">Signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public ExtractorAtom(
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
    ///     Initializes an ExtractorAtom that emits to a TypedSignalSink.
    /// </summary>
    /// <param name="typedSignals">Typed signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="contract">Optional contract override.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    public ExtractorAtom(
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        TimeSpan maxBodyDuration,
        AtomContract? contract = null,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true)
        : base(
            contract ?? AtomContract.Create(AtomKind.Extractor, AtomDeterminism.Deterministic,
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