using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Attributes;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class AttributeJobTests
{
    #region Scanner Tests

    [Fact]
    public void Scanner_FindsAttributedMethods()
    {
        var target = new TestJobHandler();
        var jobs = EphemeralJobScanner.Scan(target);

        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public void Scanner_RespectsClassAttribute()
    {
        var target = new PrefixedJobHandler();
        var jobs = EphemeralJobScanner.Scan(target);

        Assert.Single(jobs);
        Assert.Equal("myprefix.order.*", jobs[0].EffectiveTriggerSignal);
        Assert.Equal(5, jobs[0].EffectivePriority);
        Assert.Equal(2, jobs[0].EffectiveMaxConcurrency);
    }

    [Fact]
    public void Scanner_MethodAttributeOverridesClass()
    {
        var target = new OverrideJobHandler();
        var jobs = EphemeralJobScanner.Scan(target);

        Assert.Single(jobs);
        Assert.Equal(10, jobs[0].EffectivePriority); // Method override
        Assert.Equal(4, jobs[0].EffectiveMaxConcurrency); // Method override
    }

    #endregion

    #region Descriptor Tests

    [Fact]
    public void Descriptor_MatchesSignalPattern()
    {
        var target = new TestJobHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var orderJob = jobs.First(j => j.EffectiveTriggerSignal == "order.*");

        Assert.True(orderJob.Matches(new SignalEvent("order.created", 1, null, DateTimeOffset.UtcNow)));
        Assert.True(orderJob.Matches(new SignalEvent("order.updated", 2, null, DateTimeOffset.UtcNow)));
        Assert.False(orderJob.Matches(new SignalEvent("payment.processed", 3, null, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Descriptor_ExtractsKeyFromSignal()
    {
        var target = new KeyFromSignalHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        var signal = new SignalEvent("test.event", 1, "my-key", DateTimeOffset.UtcNow);
        var key = job.ExtractKey(signal, null);

        Assert.Equal("my-key", key);
    }

    [Fact]
    public void Descriptor_ExtractsKeyFromPayload()
    {
        var target = new KeyFromPayloadHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        var payload = new OrderPayload { OrderId = "ORD-123", CustomerId = "CUST-456" };
        var signal = new SignalEvent("order.created", 1, null, DateTimeOffset.UtcNow);
        var key = job.ExtractKey(signal, payload);

        Assert.Equal("ORD-123", key);
    }

    [Fact]
    public void Descriptor_ExtractsNestedKeyFromPayload()
    {
        var target = new NestedKeyHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        var payload = new OrderWithCustomer
        {
            Order = new OrderPayload { OrderId = "ORD-1" },
            Customer = new CustomerInfo { Id = "CUST-789", Name = "Test" }
        };
        var signal = new SignalEvent("order.placed", 1, null, DateTimeOffset.UtcNow);
        var key = job.ExtractKey(signal, payload);

        Assert.Equal("CUST-789", key);
    }

    [Fact]
    public void Descriptor_UsesStaticKey()
    {
        var target = new StaticKeyHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        var signal = new SignalEvent("test.event", 1, "ignored", DateTimeOffset.UtcNow);
        var key = job.ExtractKey(signal, null);

        Assert.Equal("static-key", key);
    }

    [Fact]
    public void Descriptor_Timeout()
    {
        var target = new TimeoutHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        Assert.Equal(TimeSpan.FromMilliseconds(5000), job.Timeout);
    }

    [Fact]
    public void Descriptor_Pin()
    {
        var target = new PinnedHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        Assert.True(job.IsPinned);
    }

    [Fact]
    public void Descriptor_ExpireAfter()
    {
        var target = new ExpireAfterHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        Assert.Equal(TimeSpan.FromMilliseconds(1000), job.ExpireAfter);
    }

    [Fact]
    public void Descriptor_AwaitSignals()
    {
        var target = new AwaitSignalsHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var job = jobs[0];

        Assert.NotNull(job.AwaitSignals);
        Assert.Single(job.AwaitSignals);
        Assert.Equal("prereq.*", job.AwaitSignals[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), job.AwaitTimeout);
    }

    [Fact]
    public void Descriptor_Lane_ParsesSimpleName()
    {
        var target = new LaneHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var fastJob = jobs.First(j => j.EffectiveTriggerSignal == "fast.*");

        Assert.Equal("fast", fastJob.Lane);
        Assert.Equal(0, fastJob.LaneMaxConcurrency);
    }

    [Fact]
    public void Descriptor_Lane_ParsesNameWithConcurrency()
    {
        var target = new LaneHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var slowJob = jobs.First(j => j.EffectiveTriggerSignal == "slow.*");

        Assert.Equal("slow", slowJob.Lane);
        Assert.Equal(4, slowJob.LaneMaxConcurrency);
    }

    [Fact]
    public void Descriptor_Lane_UsesClassDefault()
    {
        var target = new ClassLaneHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var readJob = jobs.First(j => j.EffectiveTriggerSignal == "read.*");

        Assert.Equal("io", readJob.Lane);
        Assert.Equal(0, readJob.LaneMaxConcurrency);
    }

    [Fact]
    public void Descriptor_Lane_MethodOverridesClass()
    {
        var target = new ClassLaneHandler();
        var jobs = EphemeralJobScanner.Scan(target);
        var computeJob = jobs.First(j => j.EffectiveTriggerSignal == "compute.*");

        Assert.Equal("cpu", computeJob.Lane);
        Assert.Equal(8, computeJob.LaneMaxConcurrency);
    }

    [Fact]
    public async Task Runner_ExposesLanes()
    {
        var sink = new SignalSink();
        var target = new LaneHandler();
        await using var runner = new EphemeralSignalJobRunner(sink, new[] { target });

        Assert.Contains("fast", runner.Lanes);
        Assert.Contains("slow", runner.Lanes);
        Assert.Equal(2, runner.Lanes.Count);
    }

    #endregion

    #region Runner Tests

    [Fact]
    public async Task Runner_ExecutesJobOnSignal()
    {
        var sink = new SignalSink();
        var target = new TestJobHandler();
        await using var runner = new EphemeralSignalJobRunner(sink, new[] { target });

        sink.Raise("order.created", "order-1");

        await Task.Delay(200);

        Assert.True(target.OrderJobExecuted);
    }

    [Fact]
    public async Task Runner_EmitsStartAndCompleteSignals()
    {
        var sink = new SignalSink();
        var target = new SignalEmittingHandler();
        await using var runner = new EphemeralSignalJobRunner(sink, new[] { target });

        sink.Raise("work.start");

        await Task.Delay(200);

        Assert.True(sink.Detect(s => s.Signal == "job.started"));
        Assert.True(sink.Detect(s => s.Signal == "job.completed"));
    }

    [Fact]
    public async Task Runner_RetriesOnFailure()
    {
        var sink = new SignalSink();
        var target = new RetryHandler();
        await using var runner = new EphemeralSignalJobRunner(sink, new[] { target });

        sink.Raise("flaky.operation");

        await Task.Delay(500);

        Assert.True(target.Attempts >= 2);
        Assert.True(sink.Detect(s => s.Signal.Contains("job.retry")));
    }

    [Fact]
    public async Task Runner_EmitsFailureSignalOnExhaustedRetries()
    {
        var sink = new SignalSink();
        var target = new AlwaysFailsHandler();
        await using var runner = new EphemeralSignalJobRunner(sink, new[] { target });

        sink.Raise("doomed.operation");

        await Task.Delay(500);

        Assert.True(sink.Detect(s => s.Signal == "on.failure"));
        Assert.True(sink.Detect(s => s.Signal.Contains("job.failed")));
    }

    #endregion

    #region Test Handlers

    private class TestJobHandler
    {
        public bool OrderJobExecuted { get; private set; }
        public bool PaymentJobExecuted { get; private set; }
        public bool NotificationJobExecuted { get; private set; }

        [EphemeralJob("order.*")]
        public Task HandleOrderAsync(CancellationToken ct)
        {
            OrderJobExecuted = true;
            return Task.CompletedTask;
        }

        [EphemeralJob("payment.*", Priority = 1)]
        public Task HandlePaymentAsync()
        {
            PaymentJobExecuted = true;
            return Task.CompletedTask;
        }

        [EphemeralJob("notification.*", Priority = 2)]
        public Task HandleNotificationAsync(SignalEvent signal)
        {
            NotificationJobExecuted = true;
            return Task.CompletedTask;
        }
    }

    [EphemeralJobs(SignalPrefix = "myprefix", DefaultPriority = 5, DefaultMaxConcurrency = 2)]
    private class PrefixedJobHandler
    {
        [EphemeralJob("order.*")]
        public Task HandleOrderAsync() => Task.CompletedTask;
    }

    [EphemeralJobs(DefaultPriority = 5, DefaultMaxConcurrency = 2)]
    private class OverrideJobHandler
    {
        [EphemeralJob("test.*", Priority = 10, MaxConcurrency = 4)]
        public Task HandleTestAsync() => Task.CompletedTask;
    }

    private class KeyFromSignalHandler
    {
        [EphemeralJob("test.*", KeyFromSignal = true)]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class KeyFromPayloadHandler
    {
        [EphemeralJob("order.*", KeyFromPayload = "OrderId")]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class NestedKeyHandler
    {
        [EphemeralJob("order.*", KeyFromPayload = "Customer.Id")]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class StaticKeyHandler
    {
        [EphemeralJob("test.*", OperationKey = "static-key")]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class TimeoutHandler
    {
        [EphemeralJob("test.*", TimeoutMs = 5000)]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class PinnedHandler
    {
        [EphemeralJob("long.*", Pin = true)]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class ExpireAfterHandler
    {
        [EphemeralJob("short.*", ExpireAfterMs = 1000)]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class AwaitSignalsHandler
    {
        [EphemeralJob("awaiting.*", AwaitSignals = new[] { "prereq.*" }, AwaitTimeoutMs = 500)]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class LaneHandler
    {
        [EphemeralJob("fast.*", Lane = "fast")]
        public Task HandleFastAsync() => Task.CompletedTask;

        [EphemeralJob("slow.*", Lane = "slow:4")]
        public Task HandleSlowAsync() => Task.CompletedTask;
    }

    [EphemeralJobs(DefaultLane = "io")]
    private class ClassLaneHandler
    {
        [EphemeralJob("read.*")]
        public Task HandleReadAsync() => Task.CompletedTask;

        [EphemeralJob("compute.*", Lane = "cpu:8")]
        public Task HandleComputeAsync() => Task.CompletedTask;
    }

    private class SignalEmittingHandler
    {
        [EphemeralJob("work.*", EmitOnStart = new[] { "job.started" }, EmitOnComplete = new[] { "job.completed" })]
        public Task HandleAsync() => Task.CompletedTask;
    }

    private class RetryHandler
    {
        public int Attempts { get; private set; }

        [EphemeralJob("flaky.*", MaxRetries = 2, RetryDelayMs = 50, SwallowExceptions = true)]
        public Task HandleAsync()
        {
            Attempts++;
            if (Attempts < 2)
                throw new Exception("Flaky failure");
            return Task.CompletedTask;
        }
    }

    private class AlwaysFailsHandler
    {
        [EphemeralJob("doomed.*", MaxRetries = 1, RetryDelayMs = 10, SwallowExceptions = true, EmitOnFailure = new[] { "on.failure" })]
        public Task HandleAsync()
        {
            throw new Exception("Always fails");
        }
    }

    private class OrderPayload
    {
        public string OrderId { get; set; } = "";
        public string CustomerId { get; set; } = "";
    }

    private class CustomerInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private class OrderWithCustomer
    {
        public OrderPayload Order { get; set; } = new();
        public CustomerInfo Customer { get; set; } = new();
    }

    #endregion
}
