using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Benchmarks;

/// <summary>
/// Benchmarks for signal cleanup performance in the new architecture where atoms own their signals.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[MarkdownExporter]
public class SignalCleanupBenchmarks
{
    private SignalSink _sink = null!;
    private EphemeralWorkCoordinator<int> _coordinator = null!;
    private List<long> _operationIds = null!;

    [Params(100, 1000, 10000)]
    public int SignalCount { get; set; }

    [Params(10, 100)]
    public int OperationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sink = new SignalSink(maxCapacity: SignalCount * 2, maxAge: TimeSpan.FromHours(1));
        _coordinator = new EphemeralWorkCoordinator<int>(
            async (item, ct) => await Task.Delay(1, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 1,
                MaxTrackedOperations = OperationCount,
                Signals = _sink
            });

        // Populate with operations and signals
        _operationIds = new List<long>();
        for (int i = 0; i < OperationCount; i++)
        {
            var opId = EphemeralIdGenerator.NextId();
            _operationIds.Add(opId);

            // Emit multiple signals per operation
            int signalsPerOp = SignalCount / OperationCount;
            for (int j = 0; j < signalsPerOp; j++)
            {
                _sink.Raise(new SignalEvent($"signal.{j}", opId, null, DateTimeOffset.UtcNow));
            }
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _coordinator.DisposeAsync();
    }

    /// <summary>
    /// Benchmark coordinator-managed signal cleanup (new architecture).
    /// When an operation is evicted, all its signals are cleared.
    /// </summary>
    [Benchmark(Description = "Clear signals for single operation (coordinator-managed)")]
    public void ClearOperationSignals()
    {
        // Simulate coordinator evicting an operation
        var opId = _operationIds[0];
        _sink.ClearOperation(opId);
    }

    /// <summary>
    /// Benchmark batch cleanup of multiple operations.
    /// </summary>
    [Benchmark(Description = "Clear signals for 10 operations (batch)")]
    public void ClearMultipleOperations()
    {
        for (int i = 0; i < Math.Min(10, _operationIds.Count); i++)
        {
            _sink.ClearOperation(_operationIds[i]);
        }
    }

    /// <summary>
    /// Benchmark age-based signal cleanup (atom-level).
    /// </summary>
    [Benchmark(Description = "Age-based cleanup (atom-level)")]
    public void AgeBasedCleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        _sink.ClearMatching(signal => signal.Timestamp < cutoff);
    }

    /// <summary>
    /// Benchmark count-based signal cleanup (atom-level).
    /// Find oldest signals and remove them.
    /// </summary>
    [Benchmark(Description = "Count-based cleanup (atom-level, keep newest 50%)")]
    public void CountBasedCleanup()
    {
        var allSignals = _sink.Sense().OrderBy(s => s.Timestamp).ToList();
        var toRemove = allSignals.Take(allSignals.Count / 2).ToList();

        foreach (var signal in toRemove)
        {
            _sink.ClearOperation(signal.OperationId);
        }
    }

    /// <summary>
    /// Benchmark querying signals from specific operations (common pattern).
    /// </summary>
    [Benchmark(Description = "Query signals by operation IDs")]
    public int QuerySignalsByOperations()
    {
        var opIds = _operationIds.Take(10).ToHashSet();
        var signals = _sink.Sense(s => opIds.Contains(s.OperationId));
        return signals.Count;
    }

    /// <summary>
    /// Benchmark window trimming performance (coordinator eviction).
    /// </summary>
    [Benchmark(Description = "Simulate window trim + signal cleanup")]
    public void SimulateWindowTrim()
    {
        // Simulate evicting 10% of operations
        int evictCount = Math.Max(1, OperationCount / 10);
        for (int i = 0; i < evictCount; i++)
        {
            var opId = _operationIds[i];
            _sink.ClearOperation(opId);
        }
    }
}

/// <summary>
/// Benchmarks comparing old vs new signal lifetime management.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[MarkdownExporter]
public class SignalLifetimeComparisonBenchmarks
{
    private SignalSink _sink = null!;
    private List<long> _operationIds = null!;

    [Params(1000, 10000)]
    public int TotalSignals { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sink = new SignalSink(maxCapacity: TotalSignals * 2, maxAge: TimeSpan.FromMinutes(5));
        _operationIds = new List<long>();

        // Create operations with signals
        for (int i = 0; i < 100; i++)
        {
            var opId = EphemeralIdGenerator.NextId();
            _operationIds.Add(opId);

            // Each operation has multiple signals
            for (int j = 0; j < TotalSignals / 100; j++)
            {
                _sink.Raise(new SignalEvent($"signal.{j}", opId, null, DateTimeOffset.UtcNow));
            }
        }
    }

    /// <summary>
    /// NEW: Coordinator-managed cleanup - clear all signals for an operation.
    /// </summary>
    [Benchmark(Baseline = true, Description = "NEW: Coordinator clears operation signals")]
    public void NewArchitecture_CoordinatorClearsOperation()
    {
        _sink.ClearOperation(_operationIds[0]);
    }

    /// <summary>
    /// OLD: SignalSink age-based cleanup (would run periodically).
    /// This is now only a safety bound, not primary expiration.
    /// </summary>
    [Benchmark(Description = "OLD: SignalSink age-based cleanup")]
    public void OldArchitecture_SinkAgeCleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        _sink.ClearMatching(s => s.Timestamp < cutoff);
    }

    /// <summary>
    /// Measure memory overhead of tracking operation→signal relationships.
    /// </summary>
    [Benchmark(Description = "Memory: Track operation→signal mapping")]
    public Dictionary<long, List<SignalEvent>> BuildOperationSignalMap()
    {
        var signals = _sink.Sense();
        var map = new Dictionary<long, List<SignalEvent>>();

        foreach (var signal in signals)
        {
            if (!map.ContainsKey(signal.OperationId))
                map[signal.OperationId] = new List<SignalEvent>();
            map[signal.OperationId].Add(signal);
        }

        return map;
    }
}

/// <summary>
/// Benchmarks for atom-level signal cleanup patterns.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[MarkdownExporter]
public class AtomSignalCleanupBenchmarks
{
    private SignalSink _sink = null!;
    private TestAtom _atom = null!;

    [Params(100, 500, 1000)]
    public int SignalsPerOperation { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _sink = new SignalSink(maxCapacity: 50000);
        _atom = new TestAtom(_sink, maxSignalCount: 5000, maxSignalAge: TimeSpan.FromMinutes(5));

        // Populate with operations
        for (int i = 0; i < 20; i++)
        {
            await _atom.EnqueueAsync(i);
        }
        await _atom.DrainAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _atom.DisposeAsync();
    }

    /// <summary>
    /// Benchmark atom-level signal cleanup (age-based).
    /// </summary>
    [Benchmark(Description = "Atom cleanup: age-based")]
    public void AtomCleanup_AgeBased()
    {
        _atom.TriggerCleanup();
    }

    /// <summary>
    /// Benchmark atom retrieving its operation IDs.
    /// </summary>
    [Benchmark(Description = "Atom: Get operation snapshot")]
    public int AtomGetSnapshot()
    {
        return _atom.Snapshot().Count;
    }

    /// <summary>
    /// Benchmark atom filtering its signals from shared sink.
    /// </summary>
    [Benchmark(Description = "Atom: Filter own signals from shared sink")]
    public int AtomFilterOwnSignals()
    {
        var snapshot = _atom.Snapshot();
        var opIds = snapshot.Select(op => op.Id).ToHashSet();
        var signals = _sink.Sense(s => opIds.Contains(s.OperationId));
        return signals.Count;
    }
}

/// <summary>
/// Test atom for benchmarking.
/// </summary>
internal class TestAtom : AtomBase<EphemeralWorkCoordinator<int>>
{
    public TestAtom(SignalSink sink, int maxSignalCount, TimeSpan maxSignalAge)
        : base(
            new EphemeralWorkCoordinator<int>(
                async (item, ct) =>
                {
                    await Task.Delay(5, ct);
                    // Emit signals
                    for (int i = 0; i < 10; i++)
                        sink.Raise($"test.signal.{i}");
                },
                new EphemeralOptions
                {
                    MaxConcurrency = 4,
                    MaxTrackedOperations = 50,
                    Signals = sink
                }),
            maxSignalCount,
            maxSignalAge)
    {
    }

    public async Task EnqueueAsync(int item)
    {
        await Coordinator.EnqueueAsync(item);
    }

    public void TriggerCleanup()
    {
        CleanupAtomSignals();
    }

    protected override void Complete() => Coordinator.Complete();
    protected override Task DrainInternalAsync(CancellationToken ct) => Coordinator.DrainAsync(ct);
    public override IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot() => Coordinator.GetSnapshot();
}
