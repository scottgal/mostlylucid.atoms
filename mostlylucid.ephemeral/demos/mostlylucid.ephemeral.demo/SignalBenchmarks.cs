using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.RateLimit;
using Mostlylucid.Ephemeral.Atoms.WindowSize;

namespace Mostlylucid.Ephemeral.Demo;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SignalBenchmarks
{
    private SignalSink _sink = null!;
    private TestAtom _atom = null!;
    private WindowSizeAtom _windowAtom = null!;
    private RateLimitAtom _rateAtom = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sink = new SignalSink(maxCapacity: 1000);

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

    [Benchmark(Description = "Signal Raise (no listeners)")]
    public void Signal_Raise_NoListeners()
    {
        var emptySink = new SignalSink();
        for (int i = 0; i < 1000; i++)
        {
            emptySink.Raise("test.signal");
        }
    }

    [Benchmark(Description = "Signal Raise (1 listener)")]
    public void Signal_Raise_OneListener()
    {
        for (int i = 0; i < 1000; i++)
        {
            _sink.Raise("test.input");
        }
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

        await using var atom1 = new TestAtom(
            sink, "A",
            listenSignals: new List<string> { "input" },
            signalResponses: new Dictionary<string, string> { { "input", "stepA" } },
            processingDelay: TimeSpan.Zero);

        await using var atom2 = new TestAtom(
            sink, "B",
            listenSignals: new List<string> { "stepA" },
            signalResponses: new Dictionary<string, string> { { "stepA", "stepB" } },
            processingDelay: TimeSpan.Zero);

        await using var atom3 = new TestAtom(
            sink, "C",
            listenSignals: new List<string> { "stepB" },
            signalResponses: new Dictionary<string, string> { { "stepB", "output" } },
            processingDelay: TimeSpan.Zero);

        for (int i = 0; i < 100; i++)
        {
            sink.Raise("input");
            await Task.Delay(1); // Allow signal propagation
        }
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

public static class BenchmarkRunner
{
    public static void RunBenchmarks()
    {
        BenchmarkDotNet.Running.BenchmarkRunner.Run<SignalBenchmarks>();
    }
}
