# Mostlylucid.Atoms

A collection of small, focused .NET libraries for common async patterns.

## Packages

### [Mostlylucid.Ephemeral](./mostlylucid.ephemeral)

**Fire... and Don't *Quite* Forget.**

Bounded, observable, self-cleaning async execution with signal-based coordination.

```bash
dotnet add package mostlylucid.ephemeral
```

| Feature | Description |
|---------|-------------|
| Bounded concurrency | Control parallel operations |
| Observable window | See running, completed, failed ops |
| Self-cleaning | Automatic memory management |
| Signal coordination | Cross-cutting observability |
| Per-key ordering | Sequential processing per entity |

**Quick Example:**

```csharp
await using var coordinator = new EphemeralWorkCoordinator<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

await coordinator.EnqueueAsync(new WorkItem("data"));
```

**Package Ecosystem:**
- Core: `mostlylucid.ephemeral`
- Atoms: fixedwork, keyedsequential, signalaware, batching, retry
- Patterns: circuitbreaker, backpressure, controlledfanout, dynamicconcurrency, + 10 more
- Bundle: `mostlylucid.ephemeral.complete` (everything in one package)

See the [full documentation](./mostlylucid.ephemeral/README.md) for details.

## Target Frameworks

All packages target:
- .NET 6.0
- .NET 7.0
- .NET 8.0
- .NET 9.0
- .NET 10.0 (preview)

## License

MIT

## Author

Scott Galloway - [mostlylucid.net](https://mostlylucid.net)
