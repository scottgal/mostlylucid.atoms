# Services (DI) Integration

This document describes how to register and use the Ephemeral coordinators and attribute runners with Microsoft.Extensions.DependencyInjection (IServiceCollection).

## Registering coordinators

The library exposes extension methods on `IServiceCollection` to register coordinators in DI so they can be injected and managed by the container.

- `services.AddEphemeralWorkCoordinator<T>(Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)`
  - Registers a singleton `EphemeralWorkCoordinator<T>` created by the provided factory.

- `services.AddEphemeralWorkCoordinator<T>(Func<T, CancellationToken, Task> body, EphemeralOptions? options = null)`
  - Overload for simple delegates that don't need services from the container.

- `services.AddEphemeralKeyedWorkCoordinator<T, TKey>(Func<T, TKey> keySelector, Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)`
  - Registers a keyed coordinator as a singleton.

- `services.AddScopedEphemeralWorkCoordinator<T>(Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)`
  - Registers a scoped coordinator instance per scope (useful when the coordinator must resolve scoped dependencies during execution).

- Named coordinator builders
  - `services.AddEphemeralWorkCoordinator<T>(string name, Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)`
  - When using named coordinators, the extensions create and store a `ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>>` in DI, and register an `IEphemeralCoordinatorFactory<T>` singleton that can create coordinators at runtime via `CreateCoordinator(name)`.

For a more idiomatic `IServiceCollection` surface you can also use the shorter helpers:

- `services.AddCoordinator<T>(Func<T, CancellationToken, Task> body, EphemeralOptions? options = null)` – a familiar `AddX` wrapper for `AddEphemeralWorkCoordinator`.
- `services.AddCoordinator<T>(Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)` – factory-aware overload.
- `services.AddScopedCoordinator<T>(Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)` – scoped lifetime variant.
- `services.AddKeyedCoordinator<T, TKey>(Func<T, TKey> keySelector, Func<T, CancellationToken, Task> body, EphemeralOptions? options = null)` and `services.AddKeyedCoordinator<T, TKey>(Func<T, TKey> keySelector, Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)` – keyed aliases.
- `services.AddScopedKeyedCoordinator<T, TKey>(Func<T, TKey> keySelector, Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory, EphemeralOptions? options = null)` – scoped keyed coordinator.

These helpers simply delegate to the `Ephemeral`-prefixed methods but make the registration/readability mirror other ASP.NET Core services.

Use the `AddCoordinator` family whenever you want the familiar `AddX` naming, including `services.AddScopedCoordinator` and the keyed variants, so coordinators can be registered just like any other service at the lifetime you need.

## Registering attribute-driven runners

`mostlylucid.ephemeral.attributes` provides helper extensions:

- `services.AddEphemeralSignalJobRunner<TJob>(EphemeralOptions? options = null, Func<IServiceProvider, SignalSink>? signalFactory = null)`
  - Registers a `SignalSink` (unless you provide one) and an `EphemeralSignalJobRunner` that instantiates the provided job types once and manages them as singletons.

- `services.AddEphemeralScopedJobRunner<TJob>(EphemeralOptions? options = null, Func<IServiceProvider, SignalSink>? signalFactory = null)`
  - Registers a scoped runner that resolves job types per invocation (for jobs depending on scoped services).

Example:

```csharp
// Program.cs
var services = new ServiceCollection();

services.AddSingleton<OrderService>();

services.AddEphemeralWorkCoordinator<Order>(
    async (order, ct) => await services.GetRequiredService<OrderService>().ProcessAsync(order, ct),
    new EphemeralOptions { MaxConcurrency = 8 });

services.AddSingleton<InvoiceJobs>();
services.AddEphemeralSignalJobRunner<InvoiceJobs>();

var provider = services.BuildServiceProvider();

var factory = provider.GetRequiredService<IEphemeralCoordinatorFactory<Order>>();
var coordinator = factory.CreateCoordinator();
await coordinator.EnqueueAsync(new Order());
```

## Notes and best practices

- Lifetime mismatch: avoid singletons capturing scoped services (inject IServiceProvider and resolve scoped services inside factory lambdas when needed).
- Disposal: singleton coordinator factories implement IDisposable and cancel created coordinators on dispose; ensure `IServiceProvider` is disposed when appropriate (e.g., by using a root `ServiceProvider` in a host).
- Named coordinators are useful when you need multiple configurations ("priority", "background", etc.) and want shared factories for runtime creation.

## Troubleshooting

- If `CreateCoordinator` throws `InvalidOperationException` for a name, ensure `AddEphemeralWorkCoordinator<T>(name, ...)` was called during registration for that name.
- If resolution fails at runtime, check that required packages and types are registered (e.g., `SignalSink` or job types). 


