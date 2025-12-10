using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.RateLimit;
using Mostlylucid.Ephemeral.Atoms.WindowSize;

namespace Mostlylucid.Ephemeral.Demo;

public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(5));

        AddExporter(MarkdownExporter.GitHub);
        AddExporter(CsvExporter.Default);
        AddExporter(HtmlExporter.Default);
        AddExporter(JsonExporter.FullCompressed);
    }
}

[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class SignalBenchmarks
{

    private SignalSink _sink = null!;
    private TestAtom _atom = null!;
    private WindowSizeAtom _windowAtom = null!;
    private RateLimitAtom _rateAtom = null!;

    private BenchmarkTestAtom _benchAtom = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sink = new SignalSink(maxCapacity: 1000);

        // Use efficient BenchmarkTestAtom instead of demo TestAtom
        _benchAtom = new BenchmarkTestAtom(_sink);

        // Legacy TestAtom for state query benchmark only
        _atom = new TestAtom(
            _sink,
            "BenchAtom",
            listenSignals: new List<string> { "test.*" },
            signalResponses: new Dictionary<string, string>
            {
                { "test.input", "test.output" }
            },
            processingDelay: TimeSpan.Zero);

        _windowAtom = new WindowSizeAtom(_sink);
        _rateAtom = new RateLimitAtom(_sink, new RateLimitOptions
        {
            InitialRatePerSecond = 1000,
            Burst = 1000
        });
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _atom.DisposeAsync();
        await _windowAtom.DisposeAsync();
        await _rateAtom.DisposeAsync();
    }

    private SignalSink _emptySink = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _emptySink = new SignalSink();
    }

    // 136.4µs → 100ms: need ~733× more operations (1K → 750K)
    [Benchmark(Description = "Signal Raise (no listeners, 750K signals) - Pure signal overhead test")]
    public void Signal_Raise_NoListeners()
    {
        for (int i = 0; i < 750_000; i++)
        {
            _emptySink.Raise("test.signal");
        }
    }

    // 913µs → 100ms: need ~110× more operations (1K → 110K)
    [Benchmark(Description = "Signal Raise (1 listener, 110K signals) - Listener invocation cost")]
    public void Signal_Raise_OneListener()
    {
        // Reset counter
        var initialCount = _benchAtom.GetCount();

        for (int i = 0; i < 110_000; i++)
        {
            _sink.Raise("test.input");
        }

        // Ensure signals were processed (prevents optimization removal)
        if (_benchAtom.GetCount() - initialCount != 110_000)
            throw new InvalidOperationException("Signal processing failed");
    }

    // 58.4µs → 100ms: need ~1700× more (4K matches → 7M matches = 1.75M iterations)
    [Benchmark(Description = "Pattern Matching (7M matches) - Glob wildcards (* and ?)")]
    public void Signal_PatternMatching()
    {
        var signals = new[] { "test.foo", "test.bar", "other.baz", "test.qux" };
        var pattern = "test.*";

        for (int i = 0; i < 1_750_000; i++)
        {
            foreach (var signal in signals)
            {
                _ = StringPatternMatcher.Matches(signal, pattern);
            }
        }
    }

    // 95.6µs → 100ms: need ~1050× more (9K → 9.5M parses = 1.05M iterations)
    [Benchmark(Description = "Command Parsing (9.5M parses) - Extract command:payload using Span")]
    public void SignalCommandMatch_Parsing()
    {
        var signals = new[] {
            "window.size.set:500",
            "rate.limit.set:10.5",
            "window.time.set:30s"
        };

        // Use zero-allocation span-based parsing
        for (int i = 0; i < 1_050_000; i++)
        {
            foreach (var signal in signals)
            {
                _ = SignalCommandMatch.TryParseSpan(signal, "window.size.set", out _);
                _ = SignalCommandMatch.TryParseSpan(signal, "rate.limit.set", out _);
                _ = SignalCommandMatch.TryParseSpan(signal, "window.time.set", out _);
            }
        }
    }

    // 6.3µs → 100ms: need ~15,800× more (100 → 1.58M acquisitions)
    [Benchmark(Description = "Rate Limiter (1.58M acquisitions) - Token bucket at 1000/sec")]
    public async Task RateLimiter_Acquire()
    {
        for (int i = 0; i < 1_580_000; i++)
        {
            using var lease = await _rateAtom.AcquireAsync();
        }
    }

    // 70.7µs → 100ms: need ~1400× more (40K → 56M queries = 14K iterations)
    [Benchmark(Description = "State Queries (56M total) - 4 methods × 14M iterations")]
    public void TestAtom_StateQuery()
    {
        for (int i = 0; i < 14_000_000; i++)
        {
            _ = _atom.GetProcessedCount();
            _ = _atom.GetLastProcessedSignal();
            _ = _atom.IsBusy();
            _ = _atom.GetState();
        }
    }

    // 125.8µs → 100ms: need ~800× more (300 → 240K commands = 80K iterations)
    [Benchmark(Description = "Window Commands (240K total) - Dynamic capacity adjustment")]
    public void WindowSizeAtom_Command()
    {
        for (int i = 0; i < 80_000; i++)
        {
            _sink.Raise("window.size.set:500");
            _sink.Raise("window.size.increase:100");
            _sink.Raise("window.size.decrease:50");
        }
    }

    // 64µs → 100ms: need ~1560× more (100 → 156K chains)
    [Benchmark(Description = "Signal Chain (3 atoms, 156K chains) - Cascading propagation A→B→C")]
    public async Task SignalChain_ThreeAtoms()
    {
        var sink = new SignalSink(maxCapacity: 200000);
        var completionTcs = new TaskCompletionSource<bool>();
        var completedCount = 0;

        await using var atom1 = new BenchmarkChainAtom(sink, "input", "stepA");
        await using var atom2 = new BenchmarkChainAtom(sink, "stepA", "stepB");
        await using var atom3 = new BenchmarkChainAtom(sink, "stepB", "output");

        // Track completion
        sink.SignalRaised += (signal) =>
        {
            if (signal.Signal == "output")
            {
                if (Interlocked.Increment(ref completedCount) == 156_000)
                    completionTcs.TrySetResult(true);
            }
        };

        for (int i = 0; i < 156_000; i++)
        {
            sink.Raise("input");
        }

        // Wait for all chains to complete (with timeout)
        await Task.WhenAny(completionTcs.Task, Task.Delay(10000));
    }

    // 276µs → 100ms: need ~360× more (10×100 → 10×36K)
    [Benchmark(Description = "Concurrent Signals (10 threads × 36K signals) - Multi-threaded stress")]
    public async Task ConcurrentSignalRaising()
    {
        var sink = new SignalSink(maxCapacity: 400000);
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            var taskId = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 36_000; j++)
                {
                    sink.Raise($"task.{taskId}.signal");
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    // 146.6µs → 100ms: need ~680× more (1K → 680K signals)
    [Benchmark(Description = "Multi-Listener (5 listeners, 680K signals) - Fan-out scaling test")]
    public void Signal_Raise_FiveListeners()
    {
        var sink = new SignalSink(maxCapacity: 700000);
        var count = 0;

        // Add 5 minimal listeners
        for (int i = 0; i < 5; i++)
        {
            sink.SignalRaised += _ => count++;
        }

        for (int i = 0; i < 680_000; i++)
        {
            sink.Raise("test.signal");
        }
    }

    // 159.3µs → 100ms: need ~630× more (1K → 630K signals)
    [Benchmark(Description = "Multi-Listener (10 listeners, 630K signals) - Linear scaling check")]
    public void Signal_Raise_TenListeners()
    {
        var sink = new SignalSink(maxCapacity: 700000);
        var count = 0;

        // Add 10 minimal listeners
        for (int i = 0; i < 10; i++)
        {
            sink.SignalRaised += _ => count++;
        }

        for (int i = 0; i < 630_000; i++)
        {
            sink.Raise("test.signal");
        }
    }

    // 255.7µs → 100ms: need ~390× more (100 → 39K chains)
    [Benchmark(Description = "Deep Chain (10 atoms, 39K chains) - Long pipeline propagation")]
    public async Task DeepSignalChain_TenAtoms()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var atoms = new List<BenchmarkChainAtom>();
        var completionTcs = new TaskCompletionSource<bool>();
        var completedCount = 0;

        // Create chain: input → step1 → step2 → ... → step10 → output
        atoms.Add(new BenchmarkChainAtom(sink, "input", "step1"));
        for (int i = 1; i < 10; i++)
        {
            atoms.Add(new BenchmarkChainAtom(sink, $"step{i}", $"step{i + 1}"));
        }
        atoms.Add(new BenchmarkChainAtom(sink, "step10", "output"));

        // Track completion
        sink.SignalRaised += (signal) =>
        {
            if (signal.Signal == "output")
            {
                if (Interlocked.Increment(ref completedCount) == 39_000)
                    completionTcs.TrySetResult(true);
            }
        };

        for (int i = 0; i < 39_000; i++)
        {
            sink.Raise("input");
        }

        await Task.WhenAny(completionTcs.Task, Task.Delay(10000));

        foreach (var atom in atoms)
        {
            await atom.DisposeAsync();
        }
    }

    // 156.7µs → 100ms: need ~640× more (20K → 12.8M matches = 640K iterations)
    [Benchmark(Description = "Complex Patterns (12.8M matches) - Multi-wildcard glob matching")]
    public void PatternMatching_Complex()
    {
        var patterns = new[] {
            "app.*.error.*",
            "system.metrics.*.cpu.*",
            "user.*.login.*",
            "cache.*.miss.*"
        };

        var signals = new[] {
            "app.web.error.500",
            "system.metrics.server1.cpu.high",
            "user.john.login.success",
            "cache.redis.miss.product",
            "other.unmatched.signal"
        };

        for (int i = 0; i < 640_000; i++)
        {
            foreach (var signal in signals)
            {
                foreach (var pattern in patterns)
                {
                    _ = StringPatternMatcher.Matches(signal, pattern);
                }
            }
        }
    }

    // 622.5µs → 100ms: need ~160× more (5K → 800K signals)
    [Benchmark(Description = "High Frequency Burst (800K signals) - Sustained throughput test")]
    public void HighFrequencyBurst()
    {
        var sink = new SignalSink(maxCapacity: 1000000);

        // Simulate burst: 800K signals as fast as possible
        for (int i = 0; i < 800_000; i++)
        {
            sink.Raise($"burst.{i % 100}");
        }
    }

    // 162.8µs → 100ms: need ~614× more (1K → 614K signals)
    [Benchmark(Description = "Window Overflow (614K ÷ 100 capacity) - Eviction mechanism stress")]
    public void SignalWindow_Overflow()
    {
        var sink = new SignalSink(maxCapacity: 100);

        // Exceed window capacity significantly
        for (int i = 0; i < 614_000; i++)
        {
            sink.Raise($"overflow.{i}");
        }
    }

    // 423.8µs → 100ms: need ~236× more (25K → 5.9M matches = 236K iterations)
    [Benchmark(Description = "Mixed Patterns (5.9M matches) - Variable depth glob complexity")]
    public void MixedPatternComplexity()
    {
        var signals = new[] {
            "simple",
            "one.level",
            "two.level.deep",
            "three.level.very.deep",
            "four.level.even.more.deep"
        };

        var patterns = new[] {
            "*",
            "*.level",
            "*.*.deep",
            "*.level.*.deep",
            "four.level.even.more.deep"
        };

        for (int i = 0; i < 236_000; i++)
        {
            foreach (var signal in signals)
            {
                foreach (var pattern in patterns)
                {
                    _ = StringPatternMatcher.Matches(signal, pattern);
                }
            }
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Large Window 10K - Capacity scaling baseline (122ns/signal)")]
    public void LargeWindow_10K()
    {
        var sink = new SignalSink(maxCapacity: 10000);

        // Fill window to capacity
        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Large Window 50K - Linear scaling test (121ns/signal expected)")]
    public void LargeWindow_50K()
    {
        var sink = new SignalSink(maxCapacity: 50000);

        // Fill window to capacity
        for (int i = 0; i < 50000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Large Window 100K - Maximum capacity stress (131ns/signal)")]
    public void LargeWindow_100K()
    {
        var sink = new SignalSink(maxCapacity: 100000);

        // Fill window to capacity
        for (int i = 0; i < 100000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Dynamic Scaling (1K→10K→50K) - Multi-phase capacity growth")]
    public void WindowScaling_Dynamic()
    {
        var sink = new SignalSink(maxCapacity: 1000);

        // Start with 1K
        for (int i = 0; i < 1000; i++)
        {
            sink.Raise($"phase1.{i}");
        }

        // Scale to 10K
        sink = new SignalSink(maxCapacity: 10000);
        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"phase2.{i}");
        }

        // Scale to 50K
        sink = new SignalSink(maxCapacity: 50000);
        for (int i = 0; i < 50000; i++)
        {
            sink.Raise($"phase3.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Large Window + Listener 10K - Listener overhead at scale")]
    public void LargeWindow_WithListener_10K()
    {
        var sink = new SignalSink(maxCapacity: 10000);
        var count = 0;

        sink.SignalRaised += _ => count++;

        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Large Window + Listener 50K - Sustained listener performance")]
    public void LargeWindow_WithListener_50K()
    {
        var sink = new SignalSink(maxCapacity: 50000);
        var count = 0;

        sink.SignalRaised += _ => count++;

        for (int i = 0; i < 50000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Eviction Stress (10K ÷ 1K window) - Continuous overflow handling")]
    public void WindowEviction_Performance()
    {
        var sink = new SignalSink(maxCapacity: 1000);

        // Continuously exceed capacity to test eviction
        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"evict.{i}");
        }
    }

    // Already >= 100ms - keep as-is
    [Benchmark(Description = "Massive Burst 100K - Ultimate throughput test (134ns/signal)")]
    public void MassiveBurst_100K()
    {
        var sink = new SignalSink(maxCapacity: 100000);

        // Stress test: 100K signals as fast as possible
        for (int i = 0; i < 100000; i++)
        {
            sink.Raise($"burst.{i % 1000}");
        }
    }

    // 154.4µs → 100ms: need ~650× more (2×500 → 2×325K)
    [Benchmark(Description = "Parallel 2 Cores (2×325K signals) - Dual-core scaling")]
    public void Parallel_2Cores()
    {
        var sink = new SignalSink(maxCapacity: 700000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2 };

        Parallel.For(0, 2, options, threadId =>
        {
            for (int i = 0; i < 325_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 227.3µs → 100ms: need ~440× more (4×250 → 4×110K)
    [Benchmark(Description = "Parallel 4 Cores (4×110K signals) - Quad-core scaling")]
    public void Parallel_4Cores()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        Parallel.For(0, 4, options, threadId =>
        {
            for (int i = 0; i < 110_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 230µs → 100ms: need ~435× more (8×125 → 8×54K)
    [Benchmark(Description = "Parallel 8 Cores (8×54K signals) - Octa-core scaling")]
    public void Parallel_8Cores()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };

        Parallel.For(0, 8, options, threadId =>
        {
            for (int i = 0; i < 54_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 190.1µs → 100ms: need ~526× more (16×62 → 16×33K)
    [Benchmark(Description = "Parallel 16 Cores (16×33K signals) - Full multi-core stress")]
    public void Parallel_16Cores()
    {
        var sink = new SignalSink(maxCapacity: 600000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        Parallel.For(0, 16, options, threadId =>
        {
            for (int i = 0; i < 33_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 3.849ms → 100ms: need ~26× more (16×1000 → 16×26K)
    [Benchmark(Description = "Parallel 16 Cores Heavy (16×26K signals) - Maximum contention test")]
    public void Parallel_16Cores_Heavy()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        Parallel.For(0, 16, options, threadId =>
        {
            for (int i = 0; i < 26_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 1.724ms → 100ms: need ~58× more (16×500 → 16×29K)
    [Benchmark(Description = "Parallel 16 Cores + Listener (16×29K) - Multi-core with fan-out")]
    public void Parallel_16Cores_WithListener()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var count = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        sink.SignalRaised += _ => Interlocked.Increment(ref count);

        Parallel.For(0, 16, options, threadId =>
        {
            for (int i = 0; i < 29_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // 333.2µs → 100ms: need ~300× more (16×250 → 16×75K matches)
    [Benchmark(Description = "Parallel Pattern Matching (16 cores × 75K matches) - Concurrent filtering")]
    public void Parallel_PatternMatching_16Cores()
    {
        var signals = new[] { "test.foo", "test.bar", "other.baz", "test.qux", "app.error", "system.warn" };
        var patterns = new[] { "test.*", "app.*", "system.*" };
        var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        Parallel.For(0, 16, options, threadId =>
        {
            for (int i = 0; i < 75_000; i++)
            {
                foreach (var signal in signals)
                {
                    foreach (var pattern in patterns)
                    {
                        _ = StringPatternMatcher.Matches(signal, pattern);
                    }
                }
            }
        });
    }

    // 733.6µs → 100ms: need ~136× more (16×50 → 16×6.8K chains)
    [Benchmark(Description = "Parallel Chain (16 cores × 6.8K chains) - Multi-threaded propagation")]
    public async Task Parallel_Chains_16Cores()
    {
        var sink = new SignalSink(maxCapacity: 500000);
        var completionCount = 0;
        var expectedCompletions = 16 * 6_800;
        var completionTcs = new TaskCompletionSource<bool>();

        await using var atom1 = new BenchmarkChainAtom(sink, "input", "step1");
        await using var atom2 = new BenchmarkChainAtom(sink, "step1", "step2");
        await using var atom3 = new BenchmarkChainAtom(sink, "step2", "output");

        sink.SignalRaised += (signal) =>
        {
            if (signal.Signal == "output")
            {
                if (Interlocked.Increment(ref completionCount) == expectedCompletions)
                    completionTcs.TrySetResult(true);
            }
        };

        var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        Parallel.For(0, 16, options, threadId =>
        {
            for (int i = 0; i < 6_800; i++)
            {
                sink.Raise("input");
            }
        });

        await Task.WhenAny(completionTcs.Task, Task.Delay(10000));
    }

    // 776.9µs → 100ms: need ~129× more
    [Benchmark(Description = "Core Scaling Test (1→2→4→8→16) - Progressive parallelism")]
    public void CoreScaling_Progressive()
    {
        var sink = new SignalSink(maxCapacity: 700000);

        // 1 core: 129K signals
        Parallel.For(0, 1, new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ =>
        {
            for (int i = 0; i < 129_000; i++) sink.Raise($"1core.{i}");
        });

        // 2 cores: 2×64.5K
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, threadId =>
        {
            for (int i = 0; i < 64_500; i++) sink.Raise($"2core.{threadId}.{i}");
        });

        // 4 cores: 4×32.2K
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, threadId =>
        {
            for (int i = 0; i < 32_200; i++) sink.Raise($"4core.{threadId}.{i}");
        });

        // 8 cores: 8×16.1K
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, threadId =>
        {
            for (int i = 0; i < 16_100; i++) sink.Raise($"8core.{threadId}.{i}");
        });

        // 16 cores: 16×8K
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, threadId =>
        {
            for (int i = 0; i < 8_000; i++) sink.Raise($"16core.{threadId}.{i}");
        });
    }

    // Additional benchmarks for 20, 24, 28, 32 cores
    [Benchmark(Description = "Parallel 20 Cores (20×26K signals) - 20-core scaling")]
    public void Parallel_20Cores()
    {
        var sink = new SignalSink(maxCapacity: 600000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 20 };

        Parallel.For(0, 20, options, threadId =>
        {
            for (int i = 0; i < 26_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    [Benchmark(Description = "Parallel 24 Cores (24×22K signals) - 24-core scaling")]
    public void Parallel_24Cores()
    {
        var sink = new SignalSink(maxCapacity: 600000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 24 };

        Parallel.For(0, 24, options, threadId =>
        {
            for (int i = 0; i < 22_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    [Benchmark(Description = "Parallel 28 Cores (28×19K signals) - 28-core scaling")]
    public void Parallel_28Cores()
    {
        var sink = new SignalSink(maxCapacity: 600000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 28 };

        Parallel.For(0, 28, options, threadId =>
        {
            for (int i = 0; i < 19_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    [Benchmark(Description = "Parallel 32 Cores (32×16K signals) - Maximum 32-core stress")]
    public void Parallel_32Cores()
    {
        var sink = new SignalSink(maxCapacity: 600000);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 32 };

        Parallel.For(0, 32, options, threadId =>
        {
            for (int i = 0; i < 16_000; i++)
            {
                sink.Raise($"thread.{threadId}.signal.{i}");
            }
        });
    }

    // ========== COORDINATOR BENCHMARKS ==========
    // These test the actual Ephemeral coordinators (the core library value)

    [Benchmark(Description = "EphemeralWorkCoordinator Enqueue (100K items, 16 concurrency) - Queue throughput")]
    public async Task WorkCoordinator_Enqueue_100K()
    {
        var tcs = new TaskCompletionSource<bool>();
        var processedCount = 0;
        var targetCount = 100_000;

        var coordinator = new EphemeralWorkCoordinator<int>(async (item, ct) =>
        {
            // Minimal work - just counting
            if (Interlocked.Increment(ref processedCount) == targetCount)
                tcs.TrySetResult(true);
            await Task.CompletedTask;
        }, new EphemeralOptions
        {
            MaxConcurrency = 16,
            MaxTrackedOperations = 100000
        });

        // Enqueue 100K items
        for (int i = 0; i < targetCount; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        // Wait for ALL work to complete
        await tcs.Task;
        await coordinator.DisposeAsync();
    }

    [Benchmark(Description = "EphemeralKeyedWorkCoordinator (10K keys × 10 items) - Per-key sequential processing")]
    public async Task KeyedCoordinator_10KKeys_10Items()
    {
        var tcs = new TaskCompletionSource<bool>();
        var processedCount = 0;
        var targetCount = 100_000;

        // T=string (the item), TKey=string (extracted from item by keySelector)
        var coordinator = new EphemeralKeyedWorkCoordinator<string, string>(
            keySelector: item => item.Split('.')[1], // Extract key from "key.123.item.5"
            body: async (item, ct) =>
            {
                if (Interlocked.Increment(ref processedCount) == targetCount)
                    tcs.TrySetResult(true);
                await Task.CompletedTask;
            },
            options: new EphemeralOptions
            {
                MaxConcurrency = 16,
                MaxTrackedOperations = 100000
            });

        // 10K keys × 10 items each = 100K total
        for (int key = 0; key < 10_000; key++)
        {
            for (int item = 0; item < 10; item++)
            {
                await coordinator.EnqueueAsync($"key.{key}.item.{item}");
            }
        }

        await tcs.Task;
        await coordinator.DisposeAsync();
    }

    [Benchmark(Description = "EphemeralForEachAsync (100K items, 16 concurrency) - Parallel collection processing")]
    public async Task ForEachAsync_100K_Concurrency16()
    {
        var items = Enumerable.Range(0, 100_000).ToList();
        var processedCount = 0;

        // EphemeralForEachAsync already waits for completion
        await items.EphemeralForEachAsync(async (item, ct) =>
        {
            Interlocked.Increment(ref processedCount);
            await Task.CompletedTask;
        }, new EphemeralOptions
        {
            MaxConcurrency = 16
        }, default(CancellationToken));

        // Verify all processed
        if (processedCount != 100_000)
            throw new InvalidOperationException($"Expected 100K, got {processedCount}");
    }

    [Benchmark(Description = "EphemeralForEachAsync (10K items, 32 concurrency) - Maximum parallelism")]
    public async Task ForEachAsync_10K_Concurrency32()
    {
        var items = Enumerable.Range(0, 10_000).ToList();
        var processedCount = 0;

        await items.EphemeralForEachAsync(async (item, ct) =>
        {
            Interlocked.Increment(ref processedCount);
            await Task.CompletedTask;
        }, new EphemeralOptions
        {
            MaxConcurrency = 32
        }, default(CancellationToken));

        if (processedCount != 10_000)
            throw new InvalidOperationException($"Expected 10K, got {processedCount}");
    }

    [Benchmark(Description = "EphemeralResultCoordinator (50K items) - Result capture + retrieval overhead")]
    public async Task ResultCoordinator_50K_Results()
    {
        var tcs = new TaskCompletionSource<bool>();
        var completedCount = 0;
        var targetCount = 50_000;

        var coordinator = new EphemeralResultCoordinator<int, string>(async (item, ct) =>
        {
            if (Interlocked.Increment(ref completedCount) == targetCount)
                tcs.TrySetResult(true);
            return $"result.{item}";
        }, new EphemeralOptions
        {
            MaxConcurrency = 16,
            MaxTrackedOperations = 50000
        });

        // Enqueue 50K items
        for (int i = 0; i < targetCount; i++)
        {
            await coordinator.EnqueueAsync(i);
        }

        await tcs.Task;
        var snapshot = coordinator.GetSnapshot();
        await coordinator.DisposeAsync();

        // Verify results captured
        if (snapshot.Count != targetCount)
            throw new InvalidOperationException($"Expected {targetCount} results, got {snapshot.Count}");
    }

    // THE FINALE: Maximum parallelism stress test with progressive scaling
    [Benchmark(Description = "🔥 FINALE: Full System Stress (1→32 cores, 2M+ signals) - Ultimate scalability test")]
    public void MaxParallelism_BallsOut_Finale()
    {
        var sink = new SignalSink(maxCapacity: 3_000_000);

        // Phase 1: 1 core baseline - 100K signals
        Parallel.For(0, 1, new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ =>
        {
            for (int i = 0; i < 100_000; i++) sink.Raise($"1core.{i}");
        });

        // Phase 2: 2 cores - 2×50K = 100K signals
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, threadId =>
        {
            for (int i = 0; i < 50_000; i++) sink.Raise($"2core.{threadId}.{i}");
        });

        // Phase 3: 4 cores - 4×50K = 200K signals
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, threadId =>
        {
            for (int i = 0; i < 50_000; i++) sink.Raise($"4core.{threadId}.{i}");
        });

        // Phase 4: 8 cores - 8×50K = 400K signals
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, threadId =>
        {
            for (int i = 0; i < 50_000; i++) sink.Raise($"8core.{threadId}.{i}");
        });

        // Phase 5: 16 cores - 16×50K = 800K signals
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, threadId =>
        {
            for (int i = 0; i < 50_000; i++) sink.Raise($"16core.{threadId}.{i}");
        });

        // Phase 6: 20 cores - 20×20K = 400K signals
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, threadId =>
        {
            for (int i = 0; i < 20_000; i++) sink.Raise($"20core.{threadId}.{i}");
        });

        // Phase 7: 24 cores - 24×20K = 480K signals
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 24 }, threadId =>
        {
            for (int i = 0; i < 20_000; i++) sink.Raise($"24core.{threadId}.{i}");
        });

        // Phase 8: 28 cores - 28×15K = 420K signals
        Parallel.For(0, 28, new ParallelOptions { MaxDegreeOfParallelism = 28 }, threadId =>
        {
            for (int i = 0; i < 15_000; i++) sink.Raise($"28core.{threadId}.{i}");
        });

        // Phase 9: BALLS OUT - 32 cores at MAXIMUM - 32×10K = 320K signals
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 32 }, threadId =>
        {
            for (int i = 0; i < 10_000; i++) sink.Raise($"32core.MAX.{threadId}.{i}");
        });

        // Total: ~2.62M signals across 9 phases testing full 1→32 core progression
    }
}

/// <summary>
/// Optimized test atom for benchmarks - no delays, minimal allocations
/// </summary>
public class BenchmarkTestAtom : IAsyncDisposable
{
    private readonly IDisposable _subscription;
    private int _count = 0;

    public BenchmarkTestAtom(SignalSink sink)
    {
        // Use lock-free Subscribe instead of event for better performance
        _subscription = sink.Subscribe(OnSignal);
    }

    private void OnSignal(SignalEvent signal)
    {
        // Minimal processing - just increment counter
        if (signal.Signal == "test.input")
        {
            _count++;
        }
    }

    public int GetCount() => _count;

    public ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        return default;
    }
}

/// <summary>
/// Optimized chain atom - immediate signal re-emission, no allocations
/// </summary>
public class BenchmarkChainAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly string _listenSignal;
    private readonly string _emitSignal;

    public BenchmarkChainAtom(SignalSink sink, string listenSignal, string emitSignal)
    {
        _sink = sink;
        _listenSignal = listenSignal;
        _emitSignal = emitSignal;
        _sink.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent signal)
    {
        // Immediate re-emission, no async, no allocations
        if (signal.Signal == _listenSignal)
        {
            _sink.Raise(_emitSignal);
        }
    }

    public ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        return default;
    }
}

/// <summary>
/// Benchmarks for multi-coordinator Dynamic Adaptive Workflow pattern.
/// Tests hotspots in priority-based failover with shared signal sink.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class DynamicWorkflowBenchmarks
{
    private SignalSink _globalSink = null!;
    private EphemeralWorkCoordinator<string> _router = null!;
    private EphemeralWorkCoordinator<string> _processor1 = null!;
    private EphemeralWorkCoordinator<string> _processor2 = null!;
    private volatile bool _proc1Healthy = true;

    [GlobalSetup]
    public async Task Setup()
    {
        _globalSink = new SignalSink(maxCapacity: 5000);

        // Primary processor (simulates 30% failure rate)
        _processor1 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                _globalSink.Raise($"processing.started:pri1:{widgetId}");
                await Task.Delay(1, ct); // Minimal delay
                var success = Random.Shared.NextDouble() > 0.3;

                if (success)
                {
                    _proc1Healthy = true;
                    _globalSink.Raise($"processing.complete:pri1:{widgetId}");
                }
                else
                {
                    _globalSink.Raise($"processing.failed:pri1:{widgetId}");
                    var recentFailures = _globalSink.Sense(s =>
                        s.Signal.Contains("processing.failed:pri1") &&
                        s.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-10)).Count;

                    if (recentFailures >= 3)
                    {
                        _proc1Healthy = false;
                        _globalSink.Raise("failover.triggered:pri1→pri2");
                    }
                }
            },
            new EphemeralOptions { MaxConcurrency = 4, Signals = _globalSink }
        );

        // Backup processor (5% failure rate)
        _processor2 = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                _globalSink.Raise($"processing.started:pri2:{widgetId}");
                await Task.Delay(2, ct); // Slightly slower
                var success = Random.Shared.NextDouble() > 0.05;

                if (success)
                {
                    _globalSink.Raise($"processing.complete:pri2:{widgetId}");
                }
                else
                {
                    _globalSink.Raise($"processing.failed:pri2:{widgetId}");
                }
            },
            new EphemeralOptions { MaxConcurrency = 4, Signals = _globalSink }
        );

        // Router coordinator
        _router = new EphemeralWorkCoordinator<string>(
            async (widgetId, ct) =>
            {
                var targetPriority = _proc1Healthy ? 1 : 2;
                _globalSink.Raise($"route.assigned:pri{targetPriority}:{widgetId}");

                if (targetPriority == 1)
                    await _processor1.EnqueueAsync(widgetId);
                else
                    await _processor2.EnqueueAsync(widgetId);
            },
            new EphemeralOptions { MaxConcurrency = 16, Signals = _globalSink }
        );

        await Task.CompletedTask;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _router?.Complete();
        _processor1?.Complete();
        _processor2?.Complete();

        if (_router != null) await _router.DrainAsync();
        if (_processor1 != null) await _processor1.DrainAsync();
        if (_processor2 != null) await _processor2.DrainAsync();

        await _router.DisposeAsync();
        await _processor1.DisposeAsync();
        await _processor2.DisposeAsync();
    }

    [Benchmark(Description = "Route 100 widgets through dynamic failover workflow")]
    [BenchmarkCategory("DynamicWorkflow")]
    public async Task RouteAndProcess100Widgets()
    {
        for (int i = 0; i < 100; i++)
        {
            await _router.EnqueueAsync($"WIDGET-{i}");
        }

        // Wait for all work to complete
        await Task.Delay(500); // Allow time for processing
    }

    [Benchmark(Description = "Signal raising hotspot - 1000 signals to shared sink")]
    [BenchmarkCategory("DynamicWorkflow")]
    public void SharedSinkRaise1000Signals()
    {
        for (int i = 0; i < 1000; i++)
        {
            _globalSink.Raise($"test.signal:{i}");
        }
    }

    [Benchmark(Description = "Signal sensing hotspot - query last 100 signals")]
    [BenchmarkCategory("DynamicWorkflow")]
    public void SignalSenseQuery()
    {
        var results = _globalSink.Sense(s =>
            s.Signal.Contains("processing") &&
            s.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-60));

        var count = results.Count; // Materialize
    }

    [Benchmark(Description = "Health check pattern - detect failures via signal query")]
    [BenchmarkCategory("DynamicWorkflow")]
    public void HealthCheckViaSignals()
    {
        var recentFailures = _globalSink.Sense(s =>
            s.Signal.Contains("processing.failed") &&
            s.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-10)).Count;

        var isHealthy = recentFailures < 3;
    }

    [Benchmark(Description = "Concurrent signal raising from 4 coordinators")]
    [BenchmarkCategory("DynamicWorkflow")]
    public async Task ConcurrentSignalRaisingFrom4Coordinators()
    {
        await Task.WhenAll(
            Task.Run(() => { for (int i = 0; i < 100; i++) _globalSink.Raise($"coord1:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 100; i++) _globalSink.Raise($"coord2:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 100; i++) _globalSink.Raise($"coord3:signal:{i}"); }),
            Task.Run(() => { for (int i = 0; i < 100; i++) _globalSink.Raise($"coord4:signal:{i}"); })
        );
    }
}

public static class BenchmarkRunner
{
    public static void RunBenchmarks()
    {
        BenchmarkDotNet.Running.BenchmarkRunner.Run<SignalBenchmarks>();
    }

    public static void RunBenchmark(string benchmarkName)
    {
        if (benchmarkName.Equals("Scoped", StringComparison.OrdinalIgnoreCase))
        {
            // Run scoped signal benchmarks from separate class
            BenchmarkDotNet.Running.BenchmarkRunner.Run<ScopedSignalBenchmarks>();
        }
        else if (benchmarkName.Equals("DynamicWorkflow", StringComparison.OrdinalIgnoreCase))
        {
            // Run dynamic workflow benchmarks
            BenchmarkDotNet.Running.BenchmarkRunner.Run<DynamicWorkflowBenchmarks>();
        }
        else
        {
            BenchmarkDotNet.Running.BenchmarkRunner.Run<SignalBenchmarks>(args: new[] { $"--filter=*{benchmarkName}*" });
        }
    }
}
