# Mostlylucid.Ephemeral.Patterns.ReactiveFanOut

Two-stage reactive pipeline that automatically throttles stage 1 when stage 2 signals backpressure.

## How It Works

1. Stage 1 receives work and fans out to Stage 2
2. When Stage 2's pending queue exceeds threshold, Stage 1 reduces concurrency
3. When Stage 2 recovers, Stage 1 scales back up

## Usage

```csharp
var pipeline = new ReactiveFanOutPipeline<Message>(
    stage2Work: async (msg, ct) => await SaveToDbAsync(msg, ct),
    preStageWork: async (msg, ct) => await ValidateAsync(msg, ct),
    stage1MaxConcurrency: 16,
    stage1MinConcurrency: 2,
    stage2MaxConcurrency: 4,
    backpressureThreshold: 100);

await pipeline.EnqueueAsync(message);
await pipeline.DrainAsync();
```

## License

MIT
