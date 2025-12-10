using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
///     Benchmarks for scoped signal architecture (SignalContext, ScopedSignalKey, ScopedSignalEmitter).
///     Tests hot-path performance of signal normalization and emission.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ScopedSignalBenchmarks
{
    private SignalContext _atomContext = default!;
    private SignalContext _coordinatorContext = default!;
    private SignalContext _sinkContext = default!;
    private SignalSink _sink = default!;
    private ScopedSignalEmitter _emitter = default!;

    [GlobalSetup]
    public void Setup()
    {
        _atomContext = new SignalContext("request", "gateway", "ResizeImageJob");
        _coordinatorContext = new SignalContext("request", "gateway", "*");
        _sinkContext = new SignalContext("request", "*", "*");

        _sink = new SignalSink(maxCapacity: 10000);
        _emitter = new ScopedSignalEmitter(_atomContext, operationId: 1, sink: _sink);
    }

    [Benchmark(Description = "SignalContext Creation (1M contexts) - Struct allocation")]
    public void SignalContext_Creation()
    {
        for (int i = 0; i < 1_000_000; i++)
        {
            var ctx = new SignalContext("request", "gateway", "ResizeImageJob");
            _ = ctx.Sink; // Prevent dead code elimination
        }
    }

    [Benchmark(Description = "ScopedSignalKey ForAtom (500K keys) - Key normalization hot path")]
    public void ScopedSignalKey_ForAtom()
    {
        for (int i = 0; i < 500_000; i++)
        {
            var key = ScopedSignalKey.ForAtom(_atomContext, "completed");
            _ = key.Sink; // Prevent dead code elimination
        }
    }

    [Benchmark(Description = "ScopedSignalKey ForCoordinator (500K keys) - Coordinator-level signals")]
    public void ScopedSignalKey_ForCoordinator()
    {
        for (int i = 0; i < 500_000; i++)
        {
            var key = ScopedSignalKey.ForCoordinator(_atomContext, "batch.completed");
            _ = key.Coordinator;
        }
    }

    [Benchmark(Description = "ScopedSignalKey ForSink (500K keys) - Sink-level signals")]
    public void ScopedSignalKey_ForSink()
    {
        for (int i = 0; i < 500_000; i++)
        {
            var key = ScopedSignalKey.ForSink(_atomContext, "health.failed");
            _ = key.Name;
        }
    }

    [Benchmark(Description = "ScopedSignalKey ToString (250K) - String formatting overhead")]
    public void ScopedSignalKey_ToString()
    {
        var key = ScopedSignalKey.ForAtom(_atomContext, "completed");

        for (int i = 0; i < 250_000; i++)
        {
            var s = key.ToString();
            _ = s.Length; // Prevent dead code elimination
        }
    }

    [Benchmark(Description = "ScopedSignalKey TryParse (250K) - Parse normalized strings")]
    public void ScopedSignalKey_TryParse()
    {
        const string signal = "request.gateway.ResizeImageJob.completed";

        for (int i = 0; i < 250_000; i++)
        {
            var success = ScopedSignalKey.TryParse(signal, out var key);
            _ = success; // Prevent dead code elimination
        }
    }

    [Benchmark(Description = "ScopedSignalEmitter Emit (100K signals) - Atom-level emission")]
    public void ScopedSignalEmitter_Emit_AtomLevel()
    {
        for (int i = 0; i < 100_000; i++)
        {
            _emitter.Emit("completed");
        }
    }

    [Benchmark(Description = "ScopedSignalEmitter EmitCoordinatorSignal (100K) - Coordinator-level")]
    public void ScopedSignalEmitter_EmitCoordinatorSignal()
    {
        for (int i = 0; i < 100_000; i++)
        {
            _emitter.EmitCoordinatorSignal("batch.completed");
        }
    }

    [Benchmark(Description = "ScopedSignalEmitter EmitSinkSignal (100K) - Sink-level")]
    public void ScopedSignalEmitter_EmitSinkSignal()
    {
        for (int i = 0; i < 100_000; i++)
        {
            _emitter.EmitSinkSignal("health.failed");
        }
    }

    [Benchmark(Description = "Mixed Emissions (100K total, 1:2:3 ratio) - Real-world usage pattern")]
    public void ScopedSignalEmitter_MixedEmissions()
    {
        // Simulate realistic mix: 50% atom, 33% coordinator, 17% sink
        for (int i = 0; i < 100_000; i++)
        {
            if (i % 6 == 0)
            {
                _emitter.EmitSinkSignal("health.check");
            }
            else if (i % 3 == 0)
            {
                _emitter.EmitCoordinatorSignal("batch.progress");
            }
            else
            {
                _emitter.Emit("completed");
            }
        }
    }

    [Benchmark(Description = "Full Pipeline (50K) - Create context → key → emit → parse")]
    public void ScopedSignal_FullPipeline()
    {
        var sink = new SignalSink(maxCapacity: 1000);

        for (int i = 0; i < 50_000; i++)
        {
            // Create context
            var ctx = new SignalContext("request", "gateway", "Job" + (i % 100));

            // Create emitter
            var emitter = new ScopedSignalEmitter(ctx, i, sink);

            // Emit signal
            emitter.Emit("completed");

            // Parse back (simulate consumer)
            var lastSignal = sink.Sense().LastOrDefault();
            if (lastSignal.Signal != null)
            {
                ScopedSignalKey.TryParse(lastSignal.Signal, out var key);
                _ = key.Atom;
            }
        }
    }

    [Benchmark(Description = "Concurrent Scoped Emissions (16 threads × 10K) - Multithreaded stress")]
    public void ScopedSignal_ConcurrentEmissions()
    {
        var sink = new SignalSink(maxCapacity: 10000);
        var tasks = new Task[16];

        for (int t = 0; t < 16; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                var ctx = new SignalContext("request", "worker" + threadId, "Job");
                var emitter = new ScopedSignalEmitter(ctx, threadId, sink);

                for (int i = 0; i < 10_000; i++)
                {
                    emitter.Emit("processed");
                }
            });
        }

        Task.WaitAll(tasks);
    }

    [Benchmark(Description = "String.Split vs Span (500K) - Parse optimization comparison")]
    public void StringSplit_vs_Span()
    {
        const string signal = "request.gateway.ResizeImageJob.completed";

        for (int i = 0; i < 500_000; i++)
        {
            // Current implementation uses String.Split
            var parts = signal.Split('.');
            _ = parts.Length;
        }
    }

    [Benchmark(Description = "String Interpolation vs Concat (250K) - ToString optimization")]
    public void StringInterpolation_vs_Concat()
    {
        for (int i = 0; i < 250_000; i++)
        {
            // Current: string interpolation in ToString()
            var s = $"request.gateway.ResizeImageJob.completed";
            _ = s.Length;
        }
    }

    [Benchmark(Description = "Allocation-Free Key Creation (500K) - Zero-alloc hot path")]
    public void AllocationFree_KeyCreation()
    {
        // Test if we can avoid allocations in key creation
        for (int i = 0; i < 500_000; i++)
        {
            var key = new ScopedSignalKey("request", "gateway", "Job", "completed");
            _ = key.Sink;
        }
    }

    [Benchmark(Description = "SignalContext Pooling (100K) - Context reuse pattern")]
    public void SignalContext_Pooling()
    {
        // Test context reuse vs recreation
        var contexts = new SignalContext[10];
        for (int i = 0; i < 10; i++)
        {
            contexts[i] = new SignalContext("request", "gateway", "Job" + i);
        }

        for (int i = 0; i < 100_000; i++)
        {
            var ctx = contexts[i % 10];
            var key = ScopedSignalKey.ForAtom(ctx, "completed");
            _ = key.ToString();
        }
    }

    [Benchmark(Description = "Emit with No Sink (100K) - Overhead measurement")]
    public void ScopedSignalEmitter_NoSink()
    {
        var emitter = new ScopedSignalEmitter(_atomContext, 1, sink: null);

        for (int i = 0; i < 100_000; i++)
        {
            emitter.Emit("completed");
        }
    }

    [Benchmark(Description = "Key Format Variations (100K each) - Short vs Long names")]
    public void KeyFormat_Variations()
    {
        // Test different signal name lengths
        for (int i = 0; i < 100_000; i++)
        {
            // Short name
            var key1 = ScopedSignalKey.ForAtom(_atomContext, "ok");

            // Medium name
            var key2 = ScopedSignalKey.ForAtom(_atomContext, "batch.completed");

            // Long name
            var key3 = ScopedSignalKey.ForAtom(_atomContext, "image.resize.thumbnail.completed");

            _ = key1.ToString() + key2.ToString() + key3.ToString();
        }
    }
}
