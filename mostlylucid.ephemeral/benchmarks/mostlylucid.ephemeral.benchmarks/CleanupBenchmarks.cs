using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Benchmarks;

/// <summary>
/// Benchmarks for the new cleanup methods on AtomBase and SignalSink.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[MarkdownExporter]
public class CleanupBenchmarks
{
    private SignalSink _sink = null!;
    private TestAtom _atom = null!;
    private List<long> _operationIds = null!;

    [Params(100, 500, 1000, 5000)]
    public int SignalCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _sink = new SignalSink(maxCapacity: SignalCount * 2);
        _atom = new TestAtom(_sink);
        _operationIds = new List<long>();

        // Create operations with signals
        for (int i = 0; i < SignalCount / 10; i++)
        {
            var opId = await _atom.EnqueueAsync(i);
            _operationIds.Add(opId);

            // Each operation gets 10 signals with varied timestamps
            for (int j = 0; j < 10; j++)
            {
                _sink.Raise(new SignalEvent(
                    $"signal.{i}.{j}",
                    opId,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-j)));
            }
        }

        await _atom.DrainAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _atom.DisposeAsync();
    }

    #region Atom Cleanup Benchmarks

    [Benchmark(Description = "Atom.Cleanup(TimeSpan) - Remove signals older than 5 minutes")]
    public int Atom_Cleanup_TimeSpan()
    {
        return _atom.Cleanup(TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "Atom.Cleanup(int) - Remove oldest 50 signals")]
    public int Atom_Cleanup_Count()
    {
        return _atom.Cleanup(50);
    }

    [Benchmark(Description = "Atom.Cleanup(string) - Remove by pattern 'signal.1.*'")]
    public int Atom_Cleanup_Pattern()
    {
        return _atom.Cleanup("signal.1.*");
    }

    #endregion

    #region SignalSink Cleanup Benchmarks

    [Benchmark(Description = "Sink.ClearOlderThan(TimeSpan) - Remove signals older than 5 minutes")]
    public int Sink_ClearOlderThan()
    {
        return _sink.ClearOlderThan(TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "Sink.ClearOldest(int) - Remove oldest 50 signals")]
    public int Sink_ClearOldest()
    {
        return _sink.ClearOldest(50);
    }

    #endregion

    #region Comparative Performance Tests

    [Benchmark(Description = "Atom cleanup vs Sink cleanup (time-based)")]
    public (int atomRemoved, int sinkRemoved) Compare_AtomVsSink_TimeBased()
    {
        var atomRemoved = _atom.Cleanup(TimeSpan.FromMinutes(5));
        var sinkRemoved = _sink.ClearOlderThan(TimeSpan.FromMinutes(5));
        return (atomRemoved, sinkRemoved);
    }

    [Benchmark(Description = "Atom cleanup vs Sink cleanup (count-based)")]
    public (int atomRemoved, int sinkRemoved) Compare_AtomVsSink_CountBased()
    {
        var atomRemoved = _atom.Cleanup(50);
        var sinkRemoved = _sink.ClearOldest(50);
        return (atomRemoved, sinkRemoved);
    }

    #endregion

    /// <summary>
    /// Test atom for benchmarking.
    /// </summary>
    private class TestAtom : AtomBase<EphemeralWorkCoordinator<int>>
    {
        public TestAtom(SignalSink sink) : base(
            new EphemeralWorkCoordinator<int>(
                async (item, ct) => await Task.Delay(1, ct),
                new EphemeralOptions
                {
                    MaxConcurrency = 4,
                    MaxTrackedOperations = 1000,
                    Signals = sink
                }))
        {
        }

        public async Task<long> EnqueueAsync(int item)
        {
            return await Coordinator.EnqueueWithIdAsync(item);
        }

        protected override void Complete() => Coordinator.Complete();
        protected override Task DrainInternalAsync(CancellationToken ct) => Coordinator.DrainAsync(ct);
        public override IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot() => Coordinator.GetSnapshot();
    }
}

/// <summary>
/// Benchmarks for cleanup method scalability under different signal counts and patterns.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[MarkdownExporter]
public class CleanupScalabilityBenchmarks
{
    private SignalSink _sink = null!;
    private List<long> _operationIds = null!;

    [Params(1000, 5000, 10000)]
    public int TotalSignals { get; set; }

    [Params(10, 50, 100)]
    public int OperationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sink = new SignalSink(maxCapacity: TotalSignals * 2);
        _operationIds = new List<long>();

        int signalsPerOp = TotalSignals / OperationCount;

        for (long i = 0; i < OperationCount; i++)
        {
            var opId = EphemeralIdGenerator.NextId();
            _operationIds.Add(opId);

            // Create varied timestamp distribution
            for (int j = 0; j < signalsPerOp; j++)
            {
                var timestamp = DateTimeOffset.UtcNow.AddSeconds(-j);
                _sink.Raise(new SignalEvent($"signal.{i}.{j}", opId, null, timestamp));
            }
        }
    }

    [Benchmark(Description = "ClearOldest - Scale test with N signals")]
    public int ClearOldest_ScaleTest()
    {
        // Clear 10% of signals
        return _sink.ClearOldest(TotalSignals / 10);
    }

    [Benchmark(Baseline = true, Description = "ClearOlderThan - Scale test with N signals")]
    public int ClearOlderThan_ScaleTest()
    {
        // Clear signals older than average age
        return _sink.ClearOlderThan(TimeSpan.FromSeconds(TotalSignals / (OperationCount * 2)));
    }
}

/// <summary>
/// Benchmarks for worst-case scenarios and edge cases.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[MarkdownExporter]
public class CleanupEdgeCaseBenchmarks
{
    private SignalSink _fullSink = null!;
    private SignalSink _sparseSink = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Full sink: Many signals, tightly packed timestamps
        _fullSink = new SignalSink(maxCapacity: 10000);
        for (int i = 0; i < 5000; i++)
        {
            _fullSink.Raise(new SignalEvent($"signal.{i}", i, null, DateTimeOffset.UtcNow.AddMilliseconds(-i)));
        }

        // Sparse sink: Few signals, widely distributed
        _sparseSink = new SignalSink(maxCapacity: 10000);
        for (int i = 0; i < 50; i++)
        {
            _sparseSink.Raise(new SignalEvent($"signal.{i}", i, null, DateTimeOffset.UtcNow.AddHours(-i)));
        }
    }

    [Benchmark(Description = "ClearOldest - Full sink (5000 signals, remove 1000)")]
    public int ClearOldest_FullSink()
    {
        return _fullSink.ClearOldest(1000);
    }

    [Benchmark(Description = "ClearOldest - Sparse sink (50 signals, remove 10)")]
    public int ClearOldest_SparseSink()
    {
        return _sparseSink.ClearOldest(10);
    }

    [Benchmark(Description = "ClearOlderThan - Full sink (5000 signals, 30s cutoff)")]
    public int ClearOlderThan_FullSink()
    {
        return _fullSink.ClearOlderThan(TimeSpan.FromSeconds(30));
    }

    [Benchmark(Description = "ClearOlderThan - Sparse sink (50 signals, 10h cutoff)")]
    public int ClearOlderThan_SparseSink()
    {
        return _sparseSink.ClearOlderThan(TimeSpan.FromHours(10));
    }

    [Benchmark(Baseline = true, Description = "ClearOldest - Request more than available")]
    public int ClearOldest_MoreThanAvailable()
    {
        return _sparseSink.ClearOldest(1000); // Only 50 available
    }

    [Benchmark(Description = "ClearOlderThan - Remove everything (zero age)")]
    public int ClearOlderThan_RemoveAll()
    {
        return _fullSink.ClearOlderThan(TimeSpan.Zero);
    }
}
