namespace Mostlylucid.Ephemeral.Attributes.Examples;

/// <summary>
///     Examples demonstrating pinned jobs (long-running operations that should never be evicted)
///     and signal composition (awaiting prerequisite signals before executing).
/// </summary>
public static class PinnedJobExamples
{
    /// <summary>
    ///     Example usage of pinned jobs with the runner.
    /// </summary>
    public static async Task RunExampleAsync()
    {
        var signals = new SignalSink();
        var errorHandler = new ErrorMonitorJob(e => Console.WriteLine($"Error: {e.Signal}"));

        await using var runner = new EphemeralSignalJobRunner(signals, new object[] { errorHandler });

        // Start the monitor (pinned, runs forever)
        signals.Raise("monitor.start");

        // Wait for monitor to be running
        await Task.Delay(200);

        // These errors will be handled by the short-lived handler
        signals.Raise("error.network");
        signals.Raise("error.timeout");

        // Let errors be processed
        await Task.Delay(500);

        // The monitor job is still pinned and visible
        // The error handler jobs have expired after 5 seconds
    }

    /// <summary>
    ///     A long-running signal watcher that monitors for error patterns.
    ///     This job runs indefinitely and should never be evicted from the coordinator.
    /// </summary>
    [EphemeralJobs]
    public class ErrorMonitorJob
    {
        private readonly Action<SignalEvent> _onError;

        public ErrorMonitorJob(Action<SignalEvent> onError)
        {
            _onError = onError;
        }

        /// <summary>
        ///     This job is triggered once on startup and runs forever, watching for errors.
        ///     Pin = true ensures the operation stays visible and is never automatically evicted.
        /// </summary>
        [EphemeralJob("monitor.start", Pin = true, EmitOnStart = new[] { "monitor.running" })]
        public async Task WatchForErrorsAsync(CancellationToken ct)
        {
            // This job runs indefinitely until cancelled
            while (!ct.IsCancellationRequested) await Task.Delay(100, ct);
        }

        /// <summary>
        ///     Handles individual errors. Short-lived, expires after 5 seconds.
        /// </summary>
        [EphemeralJob("error.*", ExpireAfterMs = 5000, KeyFromSignal = true)]
        public Task HandleErrorAsync(SignalEvent signal, CancellationToken ct)
        {
            _onError(signal);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Demonstrates signal composition - jobs that wait for prerequisites.
    /// </summary>
    [EphemeralJobs(SignalPrefix = "order")]
    public class OrderWorkflow
    {
        /// <summary>
        ///     First step: validate the order.
        /// </summary>
        [EphemeralJob("created", EmitOnComplete = new[] { "order.validated" })]
        public Task ValidateOrderAsync(SignalEvent signal)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Second step: process payment.
        ///     Waits for both order.validated AND payment.authorized signals before starting.
        /// </summary>
        [EphemeralJob("process",
            AwaitSignals = new[] { "order.validated", "payment.authorized" },
            AwaitTimeoutMs = 30000,
            EmitOnComplete = new[] { "order.paid" })]
        public Task ProcessPaymentAsync(SignalEvent signal)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Final step: ship the order.
        ///     Only starts after payment is complete.
        /// </summary>
        [EphemeralJob("ship",
            AwaitSignals = new[] { "order.paid" },
            AwaitTimeoutMs = 60000,
            EmitOnComplete = new[] { "order.shipped" })]
        public Task ShipOrderAsync(SignalEvent signal)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Example of a job that should be checked periodically and manually evicted.
    ///     Use Pin = true to keep it visible, then manually decide when to remove it.
    /// </summary>
    [EphemeralJobs]
    public class ImportantLongRunningTask
    {
        /// <summary>
        ///     A critical data sync job that runs for hours.
        ///     Pin = true keeps it visible for monitoring.
        ///     The caller can inspect its signals and decide when to evict.
        /// </summary>
        [EphemeralJob("sync.start",
            Pin = true,
            KeyFromSignal = true,
            EmitOnStart = new[] { "sync.running" },
            EmitOnComplete = new[] { "sync.done" },
            EmitOnFailure = new[] { "sync.failed" })]
        public async Task SyncDataAsync(CancellationToken ct)
        {
            // Long-running sync operation
            // Emits progress signals periodically
            for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++) await Task.Delay(1000, ct);
            // Progress would be emitted by the caller's SignalSink
        }
    }
}