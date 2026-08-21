namespace Mostlylucid.Ephemeral.Atoms.Taxonomy;

/// <summary>
///     Composable atom wrapper that combines multiple taxonomy kinds into a single contract.
/// </summary>
/// <remarks>
///     The first shard is treated as the primary kind for default output signals.
/// </remarks>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output payload type.</typeparam>
public sealed class MultiTaxonomyAtom<TInput, TOutput> : SignalDrivenAtom<TInput, TOutput>
{
    /// <summary>
    ///     Initializes a MultiTaxonomyAtom that emits to an untyped SignalSink.
    /// </summary>
    /// <param name="signals">Signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="shards">The taxonomy shards to compose.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    /// <param name="contract">Optional contract override for read/write metadata.</param>
    public MultiTaxonomyAtom(
        SignalSink signals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        IReadOnlyCollection<TaxonomyShard> shards,
        TimeSpan maxBodyDuration,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true,
        AtomContract? contract = null)
        : this(
            new TypedSignalSink<TOutput>(signals),
            handler,
            shards,
            maxBodyDuration,
            outputSignal,
            keySelector,
            options,
            emitOutputSignals,
            contract)
    {
    }

    /// <summary>
    ///     Initializes a MultiTaxonomyAtom that emits to a TypedSignalSink.
    /// </summary>
    /// <param name="typedSignals">Typed signal sink that receives output signals.</param>
    /// <param name="handler">Handler invoked for each input.</param>
    /// <param name="shards">The taxonomy shards to compose.</param>
    /// <param name="outputSignal">Optional output signal override.</param>
    /// <param name="keySelector">Optional key selector for output signals.</param>
    /// <param name="options">Optional coordinator options.</param>
    /// <param name="emitOutputSignals">When false, suppresses output signals.</param>
    /// <param name="contract">Optional contract override for read/write metadata.</param>
    public MultiTaxonomyAtom(
        TypedSignalSink<TOutput> typedSignals,
        Func<TInput, CancellationToken, Task<TOutput>> handler,
        IReadOnlyCollection<TaxonomyShard> shards,
        TimeSpan maxBodyDuration,
        string? outputSignal = null,
        Func<TInput, string?>? keySelector = null,
        EphemeralOptions? options = null,
        bool emitOutputSignals = true,
        AtomContract? contract = null)
        : base(
            ResolveContract(contract, shards),
            typedSignals,
            handler,
            maxBodyDuration,
            ResolveOutputSignal(outputSignal, shards),
            keySelector,
            options,
            emitOutputSignals)
    {
        Shards = NormalizeShards(shards);
    }

    /// <summary>
    ///     The taxonomy shards used to build the contract.
    /// </summary>
    public IReadOnlyList<TaxonomyShard> Shards { get; }

    private static IReadOnlyList<TaxonomyShard> NormalizeShards(IReadOnlyCollection<TaxonomyShard> shards)
    {
        if (shards is null)
            throw new ArgumentNullException(nameof(shards));
        if (shards.Count == 0)
            throw new ArgumentException("At least one shard is required.", nameof(shards));

        var list = new List<TaxonomyShard>(shards.Count);
        foreach (var shard in shards)
            list.Add(shard);

        return list;
    }

    private static string? ResolveOutputSignal(string? outputSignal, IReadOnlyCollection<TaxonomyShard> shards)
    {
        if (!string.IsNullOrWhiteSpace(outputSignal))
            return outputSignal;

        var primary = GetPrimaryShard(shards);
        return primary.DefaultOutputSignal;
    }

    private static AtomContract ResolveContract(AtomContract? contract, IReadOnlyCollection<TaxonomyShard> shards)
    {
        var shardList = NormalizeShards(shards);
        var expected = AtomContract.Compose(shardList);

        if (contract is null)
            return expected;

        if (!ContractMatchesShards(contract, expected, shardList))
            throw new ArgumentException("Provided contract does not match the supplied taxonomy shards.",
                nameof(contract));

        return contract;
    }

    private static bool ContractMatchesShards(
        AtomContract contract,
        AtomContract expected,
        IReadOnlyList<TaxonomyShard> shards)
    {
        if (contract.Kind != expected.Kind)
            return false;
        if (contract.Determinism != expected.Determinism)
            return false;
        if (contract.Persistence != expected.Persistence)
            return false;

        var contractKinds = new HashSet<AtomKind>(contract.Kinds);
        foreach (var shard in shards)
            if (!contractKinds.Contains(shard.Kind))
                return false;

        return true;
    }

    private static TaxonomyShard GetPrimaryShard(IReadOnlyCollection<TaxonomyShard> shards)
    {
        if (shards is null)
            throw new ArgumentNullException(nameof(shards));
        if (shards.Count == 0)
            throw new ArgumentException("At least one shard is required.", nameof(shards));

        foreach (var shard in shards)
            return shard;

        throw new InvalidOperationException("Unable to resolve the primary shard.");
    }
}