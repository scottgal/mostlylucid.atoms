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
