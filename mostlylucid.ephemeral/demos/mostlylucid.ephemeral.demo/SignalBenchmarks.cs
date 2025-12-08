using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.RateLimit;
using Mostlylucid.Ephemeral.Atoms.WindowSize;

namespace Mostlylucid.Ephemeral.Demo;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
[HtmlExporter]
[JsonExporter]
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

    [Benchmark(Description = "Signal Raise (no listeners)")]
    public void Signal_Raise_NoListeners()
    {
        for (int i = 0; i < 1000; i++)
        {
            _emptySink.Raise("test.signal");
        }
    }

    [Benchmark(Description = "Signal Raise (1 listener)")]
    public void Signal_Raise_OneListener()
    {
        // Reset counter
        var initialCount = _benchAtom.GetCount();

        for (int i = 0; i < 1000; i++)
        {
            _sink.Raise("test.input");
        }

        // Ensure signals were processed (prevents optimization removal)
        if (_benchAtom.GetCount() - initialCount != 1000)
            throw new InvalidOperationException("Signal processing failed");
    }

    [Benchmark(Description = "Signal Pattern Matching")]
    public void Signal_PatternMatching()
    {
        var signals = new[] { "test.foo", "test.bar", "other.baz", "test.qux" };
        var pattern = "test.*";

        for (int i = 0; i < 1000; i++)
        {
            foreach (var signal in signals)
            {
                _ = StringPatternMatcher.Matches(signal, pattern);
            }
        }
    }

    [Benchmark(Description = "SignalCommandMatch Parsing")]
    public void SignalCommandMatch_Parsing()
    {
        var signals = new[] {
            "window.size.set:500",
            "rate.limit.set:10.5",
            "window.time.set:30s"
        };

        for (int i = 0; i < 1000; i++)
        {
            foreach (var signal in signals)
            {
                _ = SignalCommandMatch.TryParse(signal, "window.size.set", out _);
                _ = SignalCommandMatch.TryParse(signal, "rate.limit.set", out _);
                _ = SignalCommandMatch.TryParse(signal, "window.time.set", out _);
            }
        }
    }

    [Benchmark(Description = "Rate Limiter Acquire")]
    public async Task RateLimiter_Acquire()
    {
        for (int i = 0; i < 100; i++)
        {
            using var lease = await _rateAtom.AcquireAsync();
        }
    }

    [Benchmark(Description = "TestAtom State Query")]
    public void TestAtom_StateQuery()
    {
        for (int i = 0; i < 10000; i++)
        {
            _ = _atom.GetProcessedCount();
            _ = _atom.GetLastProcessedSignal();
            _ = _atom.IsBusy();
            _ = _atom.GetState();
        }
    }

    [Benchmark(Description = "WindowSizeAtom Command")]
    public void WindowSizeAtom_Command()
    {
        for (int i = 0; i < 100; i++)
        {
            _sink.Raise("window.size.set:500");
            _sink.Raise("window.size.increase:100");
            _sink.Raise("window.size.decrease:50");
        }
    }

    [Benchmark(Description = "Signal Chain (3 atoms)")]
    public async Task SignalChain_ThreeAtoms()
    {
        var sink = new SignalSink();
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
                if (Interlocked.Increment(ref completedCount) == 100)
                    completionTcs.TrySetResult(true);
            }
        };

        for (int i = 0; i < 100; i++)
        {
            sink.Raise("input");
        }

        // Wait for all chains to complete (with timeout)
        await Task.WhenAny(completionTcs.Task, Task.Delay(5000));
    }

    [Benchmark(Description = "Concurrent Signal Raising")]
    public async Task ConcurrentSignalRaising()
    {
        var sink = new SignalSink();
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            var taskId = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    sink.Raise($"task.{taskId}.signal");
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark(Description = "Signal Raise (5 listeners)")]
    public void Signal_Raise_FiveListeners()
    {
        var sink = new SignalSink();
        var count = 0;

        // Add 5 minimal listeners
        for (int i = 0; i < 5; i++)
        {
            sink.SignalRaised += _ => count++;
        }

        for (int i = 0; i < 1000; i++)
        {
            sink.Raise("test.signal");
        }
    }

    [Benchmark(Description = "Signal Raise (10 listeners)")]
    public void Signal_Raise_TenListeners()
    {
        var sink = new SignalSink();
        var count = 0;

        // Add 10 minimal listeners
        for (int i = 0; i < 10; i++)
        {
            sink.SignalRaised += _ => count++;
        }

        for (int i = 0; i < 1000; i++)
        {
            sink.Raise("test.signal");
        }
    }

    [Benchmark(Description = "Deep Signal Chain (10 atoms)")]
    public async Task DeepSignalChain_TenAtoms()
    {
        var sink = new SignalSink();
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
                if (Interlocked.Increment(ref completedCount) == 100)
                    completionTcs.TrySetResult(true);
            }
        };

        for (int i = 0; i < 100; i++)
        {
            sink.Raise("input");
        }

        await Task.WhenAny(completionTcs.Task, Task.Delay(5000));

        foreach (var atom in atoms)
        {
            await atom.DisposeAsync();
        }
    }

    [Benchmark(Description = "Pattern Matching Complex")]
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

        for (int i = 0; i < 1000; i++)
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

    [Benchmark(Description = "High Frequency Burst")]
    public void HighFrequencyBurst()
    {
        var sink = new SignalSink(maxCapacity: 10000);

        // Simulate burst: 5000 signals as fast as possible
        for (int i = 0; i < 5000; i++)
        {
            sink.Raise($"burst.{i % 100}");
        }
    }

    [Benchmark(Description = "Signal Window Overflow")]
    public void SignalWindow_Overflow()
    {
        var sink = new SignalSink(maxCapacity: 100);

        // Exceed window capacity significantly
        for (int i = 0; i < 1000; i++)
        {
            sink.Raise($"overflow.{i}");
        }
    }

    [Benchmark(Description = "Mixed Pattern Complexity")]
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

        for (int i = 0; i < 1000; i++)
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

    [Benchmark(Description = "Large Window (10K capacity)")]
    public void LargeWindow_10K()
    {
        var sink = new SignalSink(maxCapacity: 10000);

        // Fill window to capacity
        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    [Benchmark(Description = "Large Window (50K capacity)")]
    public void LargeWindow_50K()
    {
        var sink = new SignalSink(maxCapacity: 50000);

        // Fill window to capacity
        for (int i = 0; i < 50000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    [Benchmark(Description = "Large Window (100K capacity)")]
    public void LargeWindow_100K()
    {
        var sink = new SignalSink(maxCapacity: 100000);

        // Fill window to capacity
        for (int i = 0; i < 100000; i++)
        {
            sink.Raise($"signal.{i}");
        }
    }

    [Benchmark(Description = "Window Scaling (1K → 10K → 50K)")]
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

    [Benchmark(Description = "Large Window with Listener (10K)")]
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

    [Benchmark(Description = "Large Window with Listener (50K)")]
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

    [Benchmark(Description = "Window Eviction Performance")]
    public void WindowEviction_Performance()
    {
        var sink = new SignalSink(maxCapacity: 1000);

        // Continuously exceed capacity to test eviction
        for (int i = 0; i < 10000; i++)
        {
            sink.Raise($"evict.{i}");
        }
    }

    [Benchmark(Description = "Massive Burst (100K signals)")]
    public void MassiveBurst_100K()
    {
        var sink = new SignalSink(maxCapacity: 100000);

        // Stress test: 100K signals as fast as possible
        for (int i = 0; i < 100000; i++)
        {
            sink.Raise($"burst.{i % 1000}");
        }
    }
}

/// <summary>
/// Optimized test atom for benchmarks - no delays, minimal allocations
/// </summary>
public class BenchmarkTestAtom : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private int _count = 0;

    public BenchmarkTestAtom(SignalSink sink)
    {
        _sink = sink;
        _sink.SignalRaised += OnSignal;
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
        _sink.SignalRaised -= OnSignal;
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

public static class BenchmarkRunner
{
    public static void RunBenchmarks()
    {
        BenchmarkDotNet.Running.BenchmarkRunner.Run<SignalBenchmarks>();
    }

    public static void RunBenchmark(string benchmarkName)
    {
        BenchmarkDotNet.Running.BenchmarkRunner.Run<SignalBenchmarks>(args: new[] { $"--filter=*{benchmarkName}*" });
    }
}
