using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Demo;
using Mostlylucid.Ephemeral.Atoms.RateLimit;
using Mostlylucid.Ephemeral.Atoms.WindowSize;
using Spectre.Console;

AnsiConsole.Write(
    new FigletText("Ephemeral Signals")
        .LeftJustified()
        .Color(Color.Cyan1));

AnsiConsole.MarkupLine("[grey]Interactive demonstration of the Ephemeral Signals pattern[/]\n");

while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[cyan1]Select a demo:[/]")
            .PageSize(10)
            .AddChoices(new[]
            {
                "1. Pure Notification Pattern (File Save)",
                "2. Context + Hint Pattern (Order Processing)",
                "3. Command Pattern (Window Size Control)",
                "4. Complex Multi-Step System (Rate Limiting Pipeline)",
                "5. Signal Chain Demo (Cascading Atoms)",
                "6. Circuit Breaker Pattern (Failure Handling)",
                "7. Backpressure Demo (Queue Overflow Protection)",
                "8. Metrics & Monitoring (Real-time Statistics)",
                "9. Dynamic Rate Adjustment (Adaptive Throttling)",
                "10. Live Signal Viewer",
                "B. Run Benchmarks (BenchmarkDotNet)",
                "Exit"
            }));

    if (choice == "Exit")
        break;

    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule($"[yellow]{choice}[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

    try
    {
        // Extract first character (number or letter)
        var firstChar = choice.Split('.', ' ')[0];

        switch (firstChar)
        {
            case "1":
                await RunPureNotificationDemo();
                break;
            case "2":
                await RunContextHintDemo();
                break;
            case "3":
                await RunCommandPatternDemo();
                break;
            case "4":
                await RunComplexPipelineDemo();
                break;
            case "5":
                await RunSignalChainDemo();
                break;
            case "6":
                await RunCircuitBreakerDemo();
                break;
            case "7":
                await RunBackpressureDemo();
                break;
            case "8":
                await RunMetricsMonitoringDemo();
                break;
            case "9":
                await RunDynamicRateAdjustmentDemo();
                break;
            case "10":
                await RunLiveSignalViewer();
                break;
            case "B":
                RunBenchmarks();
                return; // Exit after benchmarks
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.StackTrace ?? "")}[/]");
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]Press any key to return to menu...[/]");
    Console.ReadKey(true);
    AnsiConsole.Clear();
}

AnsiConsole.MarkupLine("[cyan1]Goodbye![/]");

void RunBenchmarks()
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule("[yellow]Benchmark Mode[/]").RuleStyle("grey"));
    AnsiConsole.WriteLine();

#if DEBUG
    AnsiConsole.MarkupLine("[red]⚠️  WARNING: Running in DEBUG mode![/]");
    AnsiConsole.MarkupLine("[yellow]Benchmarks require RELEASE mode for accurate results.[/]\n");

    AnsiConsole.MarkupLine("[cyan1]To run benchmarks:[/]");
    AnsiConsole.MarkupLine("[grey]1. Close this application[/]");
    AnsiConsole.MarkupLine("[grey]2. Run in Release mode:[/]\n");

    AnsiConsole.MarkupLine("   [yellow]dotnet run -c Release[/]\n");

    AnsiConsole.MarkupLine("[grey]Or build and run the Release executable:[/]\n");
    AnsiConsole.MarkupLine("   [yellow]dotnet build -c Release[/]");
    AnsiConsole.MarkupLine("   [yellow]cd bin/Release/net10.0[/]");

    if (OperatingSystem.IsWindows())
    {
        AnsiConsole.MarkupLine("   [yellow]mostlylucid.ephemeral.demo.exe[/]\n");
    }
    else
    {
        AnsiConsole.MarkupLine("   [yellow]./mostlylucid.ephemeral.demo[/]\n");
    }

    AnsiConsole.MarkupLine("[grey]Then select option B from the menu.[/]");
#else
    AnsiConsole.MarkupLine("[yellow]Starting BenchmarkDotNet...[/]");
    AnsiConsole.MarkupLine("[grey]This will compile and run benchmarks with memory diagnostics.[/]");
    AnsiConsole.MarkupLine("[grey]Results will show allocations, GC pressure, and performance.[/]\n");

    AnsiConsole.MarkupLine("[cyan1]Benchmarks:[/]");
    AnsiConsole.MarkupLine("[grey]- Signal Raise (no listeners)[/]");
    AnsiConsole.MarkupLine("[grey]- Signal Raise (1 listener)[/]");
    AnsiConsole.MarkupLine("[grey]- Signal Pattern Matching[/]");
    AnsiConsole.MarkupLine("[grey]- SignalCommandMatch Parsing[/]");
    AnsiConsole.MarkupLine("[grey]- Rate Limiter Acquire[/]");
    AnsiConsole.MarkupLine("[grey]- TestAtom State Query[/]");
    AnsiConsole.MarkupLine("[grey]- WindowSizeAtom Command[/]");
    AnsiConsole.MarkupLine("[grey]- Signal Chain (3 atoms)[/]");
    AnsiConsole.MarkupLine("[grey]- Concurrent Signal Raising[/]\n");

    AnsiConsole.MarkupLine("[yellow]Press any key to start benchmarks...[/]");
    Console.ReadKey(true);

    BenchmarkRunner.RunBenchmarks();
#endif
}

// ============================================================================
// Demo 1: Pure Notification Pattern
// Signal is just "hey, look at me!" - all state lives in the atom
// ============================================================================
async Task RunPureNotificationDemo()
{
    AnsiConsole.MarkupLine("[yellow]This demonstrates the PURE NOTIFICATION pattern:[/]");
    AnsiConsole.MarkupLine("[grey]- Signal: \"file.saved\" (no data in signal)[/]");
    AnsiConsole.MarkupLine("[grey]- State: Stored in FileAtom[/]");
    AnsiConsole.MarkupLine("[grey]- Listeners: Query atom for current truth[/]\n");

    var sink = new SignalSink();

    // Create logger atom to show all signals
    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 50
    });

    // Create file simulation atom
    await using var fileAtom = new TestAtom(
        sink,
        "FileAtom",
        listenSignals: new List<string> { "file.save" },
        signalResponses: new Dictionary<string, string>
        {
            { "file.save", "file.saved" }
        },
        processingDelay: TimeSpan.FromMilliseconds(200));

    // Create multiple listeners that query the atom
    var notificationListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "file.saved")
        {
            // ✅ CORRECT: Query atom for state, don't trust signal payload
            var count = fileAtom.GetProcessedCount();
            AnsiConsole.MarkupLine($"[green]✓[/] [white]Notification listener: File saved! Total files: {count}[/]");
        }
    });

    var auditListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "file.saved")
        {
            var time = fileAtom.GetLastProcessedTime();
            AnsiConsole.MarkupLine($"[blue]📋[/] [white]Audit listener: File save logged at {time:HH:mm:ss}[/]");
        }
    });

    sink.SignalRaised += notificationListener;
    sink.SignalRaised += auditListener;

    // Simulate file saves
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Simulating file saves...", async ctx =>
        {
            for (int i = 1; i <= 3; i++)
            {
                ctx.Status($"Saving file {i}/3...");
                sink.Raise("file.save");
                await Task.Delay(300);
            }
        });

    sink.SignalRaised -= notificationListener;
    sink.SignalRaised -= auditListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Signal is just notification. State queried from atom.");
}

// ============================================================================
// Demo 2: Context + Hint Pattern (Double-Safe)
// Signal carries hint for optimization, but listeners verify with atom
// ============================================================================
async Task RunContextHintDemo()
{
    AnsiConsole.MarkupLine("[yellow]This demonstrates the CONTEXT + HINT (double-safe) pattern:[/]");
    AnsiConsole.MarkupLine("[grey]- Signal: \"order.placed:ORD-123\" (hint for fast-path)[/]");
    AnsiConsole.MarkupLine("[grey]- State: Verified with OrderAtom[/]");
    AnsiConsole.MarkupLine("[grey]- Optimization: Use hint, but verify with atom for truth[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 50
    });

    // Order processing atom
    await using var orderAtom = new TestAtom(
        sink,
        "OrderAtom",
        listenSignals: new List<string> { "order.place" },
        signalResponses: new Dictionary<string, string>
        {
            { "order.place", "order.placed:ORD-123" } // Hint included
        },
        processingDelay: TimeSpan.FromMilliseconds(150));

    var emailListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal.StartsWith("order.placed"))
        {
            // ✅ DOUBLE-SAFE: Use hint for fast-path, verify with atom
            var parts = signal.Signal.Split(':');
            var hintOrderId = parts.Length > 1 ? parts[1] : null;

            // Fast-path: use hint
            if (hintOrderId != null)
            {
                AnsiConsole.MarkupLine($"[green]📧[/] [white]Email (fast-path): Sending confirmation for {hintOrderId}[/]");
            }

            // Always verify with atom for safety
            var actualCount = orderAtom.GetProcessedCount();
            AnsiConsole.MarkupLine($"[grey]   Verified: {actualCount} total orders processed[/]");
        }
    });

    sink.SignalRaised += emailListener;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Processing orders...", async ctx =>
        {
            for (int i = 1; i <= 3; i++)
            {
                ctx.Status($"Placing order {i}/3...");
                sink.Raise("order.place");
                await Task.Delay(250);
            }
        });

    sink.SignalRaised -= emailListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Hint in signal for speed, atom query for truth.");
}

// ============================================================================
// Demo 3: Command Pattern (Exception to normal pattern)
// WindowSizeAtom uses command pattern for infrastructure control
// ============================================================================
async Task RunCommandPatternDemo()
{
    AnsiConsole.MarkupLine("[yellow]This demonstrates the COMMAND pattern (exception):[/]");
    AnsiConsole.MarkupLine("[grey]- Used for: Infrastructure configuration[/]");
    AnsiConsole.MarkupLine("[grey]- Example: WindowSizeAtom adjusts SignalSink capacity[/]");
    AnsiConsole.MarkupLine("[grey]- Signal: \"window.size.set:500\" (imperative command)[/]\n");

    var sink = new SignalSink(maxCapacity: 100);

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 50
    });

    await using var windowAtom = new WindowSizeAtom(sink);

    AnsiConsole.MarkupLine($"[white]Initial capacity:[/] [cyan1]{sink.MaxCapacity}[/]");
    AnsiConsole.WriteLine();

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Adjusting window size...", async ctx =>
        {
            ctx.Status("Setting capacity to 500...");
            sink.Raise("window.size.set:500");
            await Task.Delay(100);

            AnsiConsole.MarkupLine($"[white]After set:500:[/] [cyan1]{sink.MaxCapacity}[/]");

            ctx.Status("Increasing by 200...");
            sink.Raise("window.size.increase:200");
            await Task.Delay(100);

            AnsiConsole.MarkupLine($"[white]After increase:200:[/] [cyan1]{sink.MaxCapacity}[/]");

            ctx.Status("Setting retention to 30s...");
            sink.Raise("window.time.set:30s");
            await Task.Delay(100);

            AnsiConsole.MarkupLine($"[white]Retention time:[/] [cyan1]{sink.MaxAge.TotalSeconds}s[/]");
        });

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Command pattern for infrastructure only, not domain events.");
}

// ============================================================================
// Demo 4: Complex Multi-Step System with Rate Limiting
// Shows signal chains, rate limiting, and window sizing working together
// ============================================================================
async Task RunComplexPipelineDemo()
{
    AnsiConsole.MarkupLine("[yellow]Complex pipeline demonstrating:[/]");
    AnsiConsole.MarkupLine("[grey]- Signal chains (atom → atom → atom)[/]");
    AnsiConsole.MarkupLine("[grey]- Rate limiting (1.5/s, burst 3)[/]");
    AnsiConsole.MarkupLine("[grey]- Dynamic window sizing[/]");
    AnsiConsole.MarkupLine("[grey]- Multiple listeners querying state[/]\n");

    var sink = new SignalSink(maxCapacity: 100);

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 100,
        ExcludePatterns = new List<string> { "rate.*.allowed" } // Reduce noise
    });

    await using var windowAtom = new WindowSizeAtom(sink);

    // Rate limiter: 1.5 requests per second (allows ~3 per 2s)
    await using var rateLimiter = new RateLimitAtom(sink, new RateLimitOptions
    {
        InitialRatePerSecond = 1.5,
        Burst = 3
    });

    // Pipeline atoms
    await using var validatorAtom = new TestAtom(
        sink,
        "Validator",
        listenSignals: new List<string> { "api.request" },
        signalResponses: new Dictionary<string, string>
        {
            { "api.request", "request.validated" }
        },
        processingDelay: TimeSpan.FromMilliseconds(50));

    await using var processorAtom = new TestAtom(
        sink,
        "Processor",
        listenSignals: new List<string> { "request.validated" },
        signalResponses: new Dictionary<string, string>
        {
            { "request.validated", "request.processed" }
        },
        processingDelay: TimeSpan.FromMilliseconds(100));

    await using var storageAtom = new TestAtom(
        sink,
        "Storage",
        listenSignals: new List<string> { "request.processed" },
        signalResponses: new Dictionary<string, string>
        {
            { "request.processed", "request.complete" }
        },
        processingDelay: TimeSpan.FromMilliseconds(75));

    // Stats listener
    var statsListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "request.complete")
        {
            var totalProcessed = storageAtom.GetProcessedCount();
            AnsiConsole.MarkupLine($"[green]✓[/] [white]Request complete! Total processed: {totalProcessed}[/]");
        }
    });

    sink.SignalRaised += statsListener;

    // Send requests rapidly - watch rate limiter in action
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Sending API requests...", async ctx =>
        {
            for (int i = 1; i <= 10; i++)
            {
                ctx.Status($"Sending request {i}/10...");

                // Try to acquire rate limit lease
                using var lease = await rateLimiter.AcquireAsync();

                if (lease.IsAcquired)
                {
                    sink.Raise("api.request");
                    AnsiConsole.MarkupLine($"[green]✓[/] [grey]Request {i} allowed (rate: {rateLimiter.RatePerSecond}/s, burst: {rateLimiter.Burst})[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] [grey]Request {i} rate limited[/]");
                }

                await Task.Delay(200); // Fast requests to trigger rate limit
            }
        });

    sink.SignalRaised -= statsListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Pipeline Summary:[/]");
    AnsiConsole.MarkupLine($"[grey]- Validator processed: {validatorAtom.GetProcessedCount()}[/]");
    AnsiConsole.MarkupLine($"[grey]- Processor processed: {processorAtom.GetProcessedCount()}[/]");
    AnsiConsole.MarkupLine($"[grey]- Storage processed: {storageAtom.GetProcessedCount()}[/]");
    AnsiConsole.MarkupLine($"[grey]- Rate limiter: {rateLimiter.RatePerSecond}/s, burst {rateLimiter.Burst}[/]");
}

// ============================================================================
// Demo 5: Signal Chain Demo
// Shows how atoms emit signals that trigger other atoms (cascading)
// ============================================================================
async Task RunSignalChainDemo()
{
    AnsiConsole.MarkupLine("[yellow]Signal chain demonstration:[/]");
    AnsiConsole.MarkupLine("[grey]Input → AtomA → AtomB → AtomC → Output[/]");
    AnsiConsole.MarkupLine("[grey]Each atom processes and emits next signal in chain[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 50
    });

    await using var atomA = new TestAtom(
        sink,
        "AtomA",
        listenSignals: new List<string> { "input" },
        signalResponses: new Dictionary<string, string> { { "input", "stepA.complete" } },
        processingDelay: TimeSpan.FromMilliseconds(100));

    await using var atomB = new TestAtom(
        sink,
        "AtomB",
        listenSignals: new List<string> { "stepA.complete" },
        signalResponses: new Dictionary<string, string> { { "stepA.complete", "stepB.complete" } },
        processingDelay: TimeSpan.FromMilliseconds(100));

    await using var atomC = new TestAtom(
        sink,
        "AtomC",
        listenSignals: new List<string> { "stepB.complete" },
        signalResponses: new Dictionary<string, string> { { "stepB.complete", "stepC.complete" } },
        processingDelay: TimeSpan.FromMilliseconds(100));

    var completionListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "stepC.complete")
        {
            AnsiConsole.MarkupLine($"[green]🎉 Chain complete![/] [grey](A:{atomA.GetProcessedCount()}, B:{atomB.GetProcessedCount()}, C:{atomC.GetProcessedCount()})[/]");
        }
    });

    sink.SignalRaised += completionListener;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Running signal chain...", async ctx =>
        {
            for (int i = 1; i <= 3; i++)
            {
                ctx.Status($"Chain {i}/3...");
                sink.Raise("input");
                await Task.Delay(500); // Wait for chain to complete
            }
        });

    sink.SignalRaised -= completionListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Atoms can form processing pipelines via signal chains.");
}

// ============================================================================
// Demo 6: Circuit Breaker Pattern
// Shows failure detection and automatic recovery
// ============================================================================
async Task RunCircuitBreakerDemo()
{
    AnsiConsole.MarkupLine("[yellow]Circuit Breaker Pattern demonstration:[/]");
    AnsiConsole.MarkupLine("[grey]- Detects failures via signals[/]");
    AnsiConsole.MarkupLine("[grey]- Opens circuit after threshold[/]");
    AnsiConsole.MarkupLine("[grey]- Automatic recovery after cooldown[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 100
    });

    // Simulated service that can fail
    await using var serviceAtom = new TestAtom(
        sink,
        "ServiceAtom",
        listenSignals: new List<string> { "service.call" },
        signalResponses: new Dictionary<string, string>(),
        processingDelay: TimeSpan.FromMilliseconds(50));

    // Circuit breaker state
    var circuitState = "closed"; // closed, open, half-open
    var failureCount = 0;
    var lastFailureTime = DateTime.MinValue;
    const int failureThreshold = 3;
    var cooldownPeriod = TimeSpan.FromSeconds(3);

    var circuitBreakerListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "service.call")
        {
            // Check circuit state
            if (circuitState == "open")
            {
                var timeSinceFailure = DateTime.UtcNow - lastFailureTime;
                if (timeSinceFailure > cooldownPeriod)
                {
                    circuitState = "half-open";
                    AnsiConsole.MarkupLine("[yellow]🔄 Circuit entering HALF-OPEN state (testing recovery)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]⛔ Circuit OPEN - call rejected[/]");
                    sink.Raise("circuit.open.rejected");
                    return;
                }
            }

            // Simulate success/failure (70% success rate)
            var random = new Random();
            var success = random.Next(100) < 70;

            if (success)
            {
                sink.Raise("service.success");
                AnsiConsole.MarkupLine($"[green]✓[/] [white]Service call succeeded (failures: {failureCount})[/]");

                if (circuitState == "half-open")
                {
                    circuitState = "closed";
                    failureCount = 0;
                    AnsiConsole.MarkupLine("[green]✅ Circuit CLOSED - recovery complete[/]");
                }
            }
            else
            {
                failureCount++;
                lastFailureTime = DateTime.UtcNow;
                sink.Raise("service.failure");
                AnsiConsole.MarkupLine($"[red]✗[/] [grey]Service call failed ({failureCount}/{failureThreshold})[/]");

                if (failureCount >= failureThreshold && circuitState == "closed")
                {
                    circuitState = "open";
                    AnsiConsole.MarkupLine($"[red]🔴 Circuit OPEN - too many failures! Cooldown: {cooldownPeriod.TotalSeconds}s[/]");
                    sink.Raise("circuit.open");
                }
            }
        }
    });

    sink.SignalRaised += circuitBreakerListener;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Simulating service calls...", async ctx =>
        {
            for (int i = 1; i <= 20; i++)
            {
                ctx.Status($"Call {i}/20...");
                sink.Raise("service.call");
                await Task.Delay(300);
            }
        });

    sink.SignalRaised -= circuitBreakerListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[yellow]Final State:[/] Circuit is [cyan1]{circuitState.ToUpper()}[/]");
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Circuit breaker protects system from cascading failures.");
}

// ============================================================================
// Demo 7: Backpressure Demo
// Shows queue overflow protection via signal-based flow control
// ============================================================================
async Task RunBackpressureDemo()
{
    AnsiConsole.MarkupLine("[yellow]Backpressure demonstration:[/]");
    AnsiConsole.MarkupLine("[grey]- Producer generates data rapidly[/]");
    AnsiConsole.MarkupLine("[grey]- Consumer processes slowly[/]");
    AnsiConsole.MarkupLine("[grey]- Backpressure signals slow down producer[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 100,
        ExcludePatterns = new List<string> { "queue.item.added" }
    });

    var queue = new Queue<int>();
    const int maxQueueSize = 5;
    var isBackpressureActive = false;

    // Producer
    await using var producerAtom = new TestAtom(
        sink,
        "Producer",
        listenSignals: new List<string> { "produce.item" },
        signalResponses: new Dictionary<string, string>(),
        processingDelay: TimeSpan.FromMilliseconds(10));

    // Consumer (slow)
    await using var consumerAtom = new TestAtom(
        sink,
        "Consumer",
        listenSignals: new List<string> { "queue.item.added" },
        signalResponses: new Dictionary<string, string> { { "queue.item.added", "item.consumed" } },
        processingDelay: TimeSpan.FromMilliseconds(200));

    var queueListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "produce.item" && !isBackpressureActive)
        {
            queue.Enqueue(queue.Count + 1);
            sink.Raise("queue.item.added");
            AnsiConsole.MarkupLine($"[cyan]📦[/] [white]Produced item (queue: {queue.Count}/{maxQueueSize})[/]");

            if (queue.Count >= maxQueueSize)
            {
                isBackpressureActive = true;
                sink.Raise("backpressure.start");
                AnsiConsole.MarkupLine($"[yellow]⚠️  BACKPRESSURE ACTIVE - queue full ({queue.Count}/{maxQueueSize})[/]");
            }
        }
        else if (signal.Signal == "produce.item" && isBackpressureActive)
        {
            AnsiConsole.MarkupLine("[red]🛑[/] [grey]Production blocked by backpressure[/]");
            sink.Raise("backpressure.blocked");
        }
        else if (signal.Signal == "item.consumed")
        {
            if (queue.Count > 0)
            {
                var item = queue.Dequeue();
                AnsiConsole.MarkupLine($"[green]✓[/] [white]Consumed item {item} (queue: {queue.Count}/{maxQueueSize})[/]");

                if (isBackpressureActive && queue.Count < maxQueueSize / 2)
                {
                    isBackpressureActive = false;
                    sink.Raise("backpressure.end");
                    AnsiConsole.MarkupLine($"[green]✅ BACKPRESSURE RELEASED - queue draining ({queue.Count}/{maxQueueSize})[/]");
                }
            }
        }
    });

    sink.SignalRaised += queueListener;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Running producer/consumer...", async ctx =>
        {
            for (int i = 1; i <= 15; i++)
            {
                ctx.Status($"Producing item {i}/15...");
                sink.Raise("produce.item");
                await Task.Delay(100);
            }

            // Let consumer drain
            ctx.Status("Draining queue...");
            await Task.Delay(2000);
        });

    sink.SignalRaised -= queueListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[yellow]Final Queue Size:[/] {queue.Count}");
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Backpressure prevents queue overflow via flow control.");
}

// ============================================================================
// Demo 8: Metrics & Monitoring
// Real-time statistics tracking via signal aggregation
// ============================================================================
async Task RunMetricsMonitoringDemo()
{
    AnsiConsole.MarkupLine("[yellow]Metrics & Monitoring demonstration:[/]");
    AnsiConsole.MarkupLine("[grey]- Track request counts, success/failure rates[/]");
    AnsiConsole.MarkupLine("[grey]- Calculate latency percentiles[/]");
    AnsiConsole.MarkupLine("[grey]- Real-time dashboard updates[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = false, // We'll show dashboard instead
        WindowSize = 200
    });

    // Metrics tracking
    var totalRequests = 0;
    var successCount = 0;
    var failureCount = 0;
    var latencies = new List<double>();
    var random = new Random();

    var metricsListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal.StartsWith("request."))
        {
            totalRequests++;
            var latency = random.Next(10, 500);
            latencies.Add(latency);

            if (signal.Signal == "request.success")
            {
                successCount++;
            }
            else if (signal.Signal == "request.failure")
            {
                failureCount++;
            }
        }
    });

    sink.SignalRaised += metricsListener;

    // Request generator
    await using var requestAtom = new TestAtom(
        sink,
        "RequestHandler",
        listenSignals: new List<string> { "request.incoming" },
        signalResponses: new Dictionary<string, string>(),
        processingDelay: TimeSpan.FromMilliseconds(50));

    var dashboardTask = Task.Run(async () =>
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300);

            // Simulate requests
            var success = random.Next(100) < 85; // 85% success rate
            if (success)
            {
                sink.Raise("request.success");
            }
            else
            {
                sink.Raise("request.failure");
            }

            // Update dashboard every 3 requests
            if (i % 3 == 0 && latencies.Count > 0)
            {
                var sorted = latencies.OrderBy(x => x).ToList();
                var p50 = sorted[sorted.Count / 2];
                var p95 = sorted[(int)(sorted.Count * 0.95)];
                var p99 = sorted[(int)(sorted.Count * 0.99)];

                var successRate = totalRequests > 0 ? (successCount * 100.0 / totalRequests) : 0;

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Metric")
                    .AddColumn("Value");

                table.AddRow("Total Requests", $"[cyan1]{totalRequests}[/]");
                table.AddRow("Success Rate", $"[green]{successRate:F1}%[/]");
                table.AddRow("Successes", $"[green]{successCount}[/]");
                table.AddRow("Failures", $"[red]{failureCount}[/]");
                table.AddRow("Latency P50", $"[yellow]{p50:F0}ms[/]");
                table.AddRow("Latency P95", $"[yellow]{p95:F0}ms[/]");
                table.AddRow("Latency P99", $"[red]{p99:F0}ms[/]");

                AnsiConsole.Clear();
                AnsiConsole.Write(new Rule("[yellow]Real-Time Metrics Dashboard[/]").RuleStyle("grey"));
                AnsiConsole.WriteLine();
                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
            }
        }
    });

    await dashboardTask;
    sink.SignalRaised -= metricsListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Signals enable real-time metrics aggregation and monitoring.");
}

// ============================================================================
// Demo 9: Dynamic Rate Adjustment
// Adaptive throttling based on system load signals
// ============================================================================
async Task RunDynamicRateAdjustmentDemo()
{
    AnsiConsole.MarkupLine("[yellow]Dynamic Rate Adjustment demonstration:[/]");
    AnsiConsole.MarkupLine("[grey]- Monitor system load via signals[/]");
    AnsiConsole.MarkupLine("[grey]- Automatically adjust rate limits[/]");
    AnsiConsole.MarkupLine("[grey]- Scale up during low load, down during high load[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 100,
        ExcludePatterns = new List<string> { "load.check" }
    });

    await using var rateLimiter = new RateLimitAtom(sink, new RateLimitOptions
    {
        InitialRatePerSecond = 5,
        Burst = 10
    });

    var currentLoad = 0.3; // 30% load
    var random = new Random();

    var loadMonitorListener = new Action<SignalEvent>(signal =>
    {
        if (signal.Signal == "load.check")
        {
            // Simulate load fluctuation
            currentLoad += (random.NextDouble() - 0.5) * 0.2;
            currentLoad = Math.Clamp(currentLoad, 0.1, 0.95);

            var loadPercent = currentLoad * 100;
            var color = currentLoad < 0.5 ? "green" : currentLoad < 0.75 ? "yellow" : "red";

            AnsiConsole.MarkupLine($"[grey]📊 System load: [{color}]{loadPercent:F0}%[/][/]");

            // Adjust rate based on load
            var targetRate = currentLoad < 0.5 ? 10.0 : currentLoad < 0.75 ? 5.0 : 2.0;
            var currentRate = rateLimiter.RatePerSecond;

            if (Math.Abs(currentRate - targetRate) > 0.5)
            {
                sink.Raise($"rate.limit.set:{targetRate}");
                AnsiConsole.MarkupLine($"[cyan]⚙️  Adjusted rate: {currentRate:F1}/s → {targetRate:F1}/s[/]");
            }
        }
    });

    sink.SignalRaised += loadMonitorListener;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Monitoring system load...", async ctx =>
        {
            for (int i = 1; i <= 15; i++)
            {
                ctx.Status($"Cycle {i}/15 (rate: {rateLimiter.RatePerSecond:F1}/s)...");

                sink.Raise("load.check");

                // Try to acquire rate limit
                using var lease = await rateLimiter.AcquireAsync();
                if (lease.IsAcquired)
                {
                    AnsiConsole.MarkupLine($"[green]✓[/] [white]Request allowed ({rateLimiter.RatePerSecond:F1}/s, burst:{rateLimiter.Burst})[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]✗[/] [grey]Request rate limited[/]");
                }

                await Task.Delay(400);
            }
        });

    sink.SignalRaised -= loadMonitorListener;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[yellow]Final Rate:[/] {rateLimiter.RatePerSecond:F1}/s (burst: {rateLimiter.Burst})");
    AnsiConsole.MarkupLine("[yellow]Key Takeaway:[/] Rate limits can adapt dynamically to system conditions.");
}

// ============================================================================
// Demo 10: Live Signal Viewer
// Interactive viewer showing all signals in real-time
// ============================================================================
async Task RunLiveSignalViewer()
{
    AnsiConsole.MarkupLine("[yellow]Live signal viewer - generating random signals...[/]");
    AnsiConsole.MarkupLine("[grey]Press any key to stop[/]\n");

    var sink = new SignalSink();

    await using var logger = new ConsoleSignalLoggerAtom(sink, new ConsoleSignalLoggerOptions
    {
        AutoOutput = true,
        WindowSize = 200
    });

    var cts = new CancellationTokenSource();
    var signalTypes = new[]
    {
        "info.user.login",
        "info.user.logout",
        "warning.rate.limit.approaching",
        "error.database.timeout",
        "debug.cache.miss",
        "trace.sql.query",
        "info.file.uploaded",
        "info.payment.processed",
        "warning.disk.space.low",
        "error.api.unavailable"
    };

    var random = new Random();

    var signalTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var signal = signalTypes[random.Next(signalTypes.Length)];
            sink.Raise(signal);
            await Task.Delay(random.Next(100, 500), cts.Token);
        }
    }, cts.Token);

    Console.ReadKey(true);
    cts.Cancel();

    try
    {
        await signalTask;
    }
    catch (OperationCanceledException)
    {
        // Expected
    }

    AnsiConsole.WriteLine();
    logger.DumpWindow();
}
