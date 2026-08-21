using Mostlylucid.Ephemeral.Atoms.Retry;
using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;
using Xunit;

namespace Mostlylucid.Ephemeral.Atoms.Retry.Tests;

public class RetryAtomTests
{
    [Fact]
    public void Constructor_RejectsNullBody()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RetryAtom<int>(null!, BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)),
                TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Constructor_RejectsNullBackoff()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RetryAtom<int>((_, _) => Task.CompletedTask, null!, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveMaxAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryAtom<int>((_, _) => Task.CompletedTask,
                BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)), TimeSpan.FromSeconds(5), maxAttempts: 0));
    }

    [Fact]
    public void Constructor_RejectsBreakerWithoutSignals()
    {
        var breaker = new SignalBasedCircuitBreaker();
        Assert.Throws<ArgumentException>(() =>
            new RetryAtom<int>((_, _) => Task.CompletedTask,
                BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)), TimeSpan.FromSeconds(5), breaker: breaker));
    }

    [Fact]
    public async Task SucceedsFirstAttempt_NoRetrySignalsRaised()
    {
        var sink = new SignalSink();
        var calls = 0;

        await using var atom = new RetryAtom<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)),
            TimeSpan.FromSeconds(5),
            signals: sink);

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        Assert.Equal(1, calls);
        Assert.DoesNotContain(sink.Sense(), e => e.Signal is RetryAtom<int>.AttemptFailedSignal
            or RetryAtom<int>.ExhaustedSignal or RetryAtom<int>.SucceededAfterRetrySignal);
    }

    [Fact]
    public async Task RetriesOnFailure_SucceedsWithinMaxAttempts_EmitsSignals()
    {
        var sink = new SignalSink();
        var calls = 0;

        await using var atom = new RetryAtom<int>(
            (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n < 3) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            },
            BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)),
            TimeSpan.FromSeconds(5),
            maxAttempts: 5,
            signals: sink);

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        Assert.Equal(3, calls);
        var signals = sink.Sense();
        Assert.Equal(2, signals.Count(e => e.Signal == RetryAtom<int>.AttemptFailedSignal));
        Assert.Single(signals, e => e.Signal == RetryAtom<int>.SucceededAfterRetrySignal);
        Assert.DoesNotContain(signals, e => e.Signal == RetryAtom<int>.ExhaustedSignal);
    }

    [Fact]
    public async Task ExhaustsAttempts_ThrowsAndEmitsExhaustedSignal()
    {
        var sink = new SignalSink();
        var calls = 0;

        await using var atom = new RetryAtom<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("permanent");
            },
            BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)),
            TimeSpan.FromSeconds(5),
            maxAttempts: 3,
            signals: sink);

        await atom.EnqueueAsync(1);
        await atom.DrainAsync();

        Assert.Equal(3, calls);
        var signals = sink.Sense();
        Assert.Equal(2, signals.Count(e => e.Signal == RetryAtom<int>.AttemptFailedSignal));
        Assert.Single(signals, e => e.Signal == RetryAtom<int>.ExhaustedSignal);
        Assert.DoesNotContain(signals, e => e.Signal == RetryAtom<int>.SucceededAfterRetrySignal);
    }

    [Fact]
    public void BackoffStrategies_ComputeExpectedDelays()
    {
        var constant = BackoffStrategies.Constant(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), constant(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), constant(5));

        var linear = BackoffStrategies.Linear(TimeSpan.FromMilliseconds(50));
        Assert.Equal(TimeSpan.FromMilliseconds(50), linear(1));
        Assert.Equal(TimeSpan.FromMilliseconds(150), linear(3));

        var exponential = BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), exponential(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), exponential(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), exponential(3));
    }

    [Fact]
    public void BackoffStrategies_ExponentialWithJitter_StaysWithinRatio()
    {
        var strategy = BackoffStrategies.ExponentialWithJitter(TimeSpan.FromMilliseconds(100), jitterRatio: 0.2,
            random: new Random(42));

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var baseMs = 100 * Math.Pow(2, attempt - 1);
            var delay = strategy(attempt).TotalMilliseconds;
            Assert.InRange(delay, baseMs * 0.8, baseMs * 1.2);
        }
    }

    [Fact]
    public async Task Breaker_WhenOpen_ThrowsCircuitOpenException_WithoutEnqueueing()
    {
        var sink = new SignalSink();
        var breaker = new SignalBasedCircuitBreaker(RetryAtom<int>.ExhaustedSignal, threshold: 1);
        var calls = 0;

        await using var atom = new RetryAtom<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("permanent");
            },
            BackoffStrategies.Constant(TimeSpan.FromMilliseconds(1)),
            TimeSpan.FromSeconds(5),
            maxAttempts: 1,
            signals: sink,
            breaker: breaker);

        // First item exhausts immediately (maxAttempts: 1) and raises ExhaustedSignal, which the
        // breaker (threshold: 1) treats as tripping open. Deliberately not calling DrainAsync()
        // here — that calls Complete(), permanently closing intake, which would make the next
        // EnqueueAsync fail regardless of breaker state and defeat what this test checks.
        await atom.EnqueueAsync(1);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (sink.Sense().All(e => e.Signal != RetryAtom<int>.ExhaustedSignal) && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        await Assert.ThrowsAsync<CircuitOpenException>(async () => await atom.EnqueueAsync(2));

        // The breaker blocked intake before the coordinator ever ran item 2's body.
        Assert.Equal(1, calls);
    }
}
