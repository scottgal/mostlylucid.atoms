using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mostlylucid.Helpers.Ephemeral;

/// <summary>
/// Factory interface for creating named/typed ephemeral work coordinators.
/// Similar to IHttpClientFactory - each named coordinator shares configuration but has independent state.
/// </summary>
public interface IEphemeralCoordinatorFactory<T>
{
    /// <summary>
    /// Gets or creates a coordinator with the specified name.
    /// Coordinators are cached by name within the factory's scope.
    /// </summary>
    EphemeralWorkCoordinator<T> CreateCoordinator(string name = "");
}

/// <summary>
/// Factory interface for creating named/typed keyed ephemeral work coordinators.
/// </summary>
public interface IEphemeralKeyedCoordinatorFactory<T, TKey>
    where TKey : notnull
{
    /// <summary>
    /// Gets or creates a keyed coordinator with the specified name.
    /// </summary>
    EphemeralKeyedWorkCoordinator<T, TKey> CreateCoordinator(string name = "");
}

/// <summary>
/// Configuration for a named coordinator.
/// </summary>
public sealed class EphemeralCoordinatorConfiguration<T>
{
    internal Func<IServiceProvider, Func<T, CancellationToken, Task>>? BodyFactory { get; set; }
    internal EphemeralOptions? Options { get; set; }
}

/// <summary>
/// Configuration for a named keyed coordinator.
/// </summary>
public sealed class EphemeralKeyedCoordinatorConfiguration<T, TKey>
    where TKey : notnull
{
    internal Func<T, TKey>? KeySelector { get; set; }
    internal Func<IServiceProvider, Func<T, CancellationToken, Task>>? BodyFactory { get; set; }
    internal EphemeralOptions? Options { get; set; }
}

/// <summary>
/// Builder for configuring named ephemeral coordinators.
/// Similar to IHttpClientBuilder.
/// </summary>
public interface IEphemeralCoordinatorBuilder<T>
{
    /// <summary>
    /// The name of the coordinator being configured.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The service collection.
    /// </summary>
    IServiceCollection Services { get; }
}

internal sealed class EphemeralCoordinatorBuilder<T> : IEphemeralCoordinatorBuilder<T>
{
    public EphemeralCoordinatorBuilder(string name, IServiceCollection services)
    {
        Name = name;
        Services = services;
    }

    public string Name { get; }
    public IServiceCollection Services { get; }
}

internal sealed class EphemeralCoordinatorFactory<T> : IEphemeralCoordinatorFactory<T>, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>> _configurations;
    private readonly ConcurrentDictionary<string, Lazy<EphemeralWorkCoordinator<T>>> _coordinators = new();
    private bool _disposed;

    public EphemeralCoordinatorFactory(
        IServiceProvider serviceProvider,
        ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>> configurations)
    {
        _serviceProvider = serviceProvider;
        _configurations = configurations;
    }

    public EphemeralWorkCoordinator<T> CreateCoordinator(string name = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _coordinators.GetOrAdd(name, n => new Lazy<EphemeralWorkCoordinator<T>>(() =>
        {
            if (!_configurations.TryGetValue(n, out var config))
            {
                throw new InvalidOperationException(
                    $"No coordinator configuration found for name '{n}'. " +
                    $"Call AddEphemeralWorkCoordinator<{typeof(T).Name}>(\"{n}\", ...) during registration.");
            }

            var body = config.BodyFactory!(_serviceProvider);
            return new EphemeralWorkCoordinator<T>(body, config.Options);
        })).Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var lazy in _coordinators.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Cancel();
            }
        }
    }
}

internal sealed class EphemeralKeyedCoordinatorFactory<T, TKey> : IEphemeralKeyedCoordinatorFactory<T, TKey>, IDisposable
    where TKey : notnull
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, EphemeralKeyedCoordinatorConfiguration<T, TKey>> _configurations;
    private readonly ConcurrentDictionary<string, Lazy<EphemeralKeyedWorkCoordinator<T, TKey>>> _coordinators = new();
    private bool _disposed;

    public EphemeralKeyedCoordinatorFactory(
        IServiceProvider serviceProvider,
        ConcurrentDictionary<string, EphemeralKeyedCoordinatorConfiguration<T, TKey>> configurations)
    {
        _serviceProvider = serviceProvider;
        _configurations = configurations;
    }

    public EphemeralKeyedWorkCoordinator<T, TKey> CreateCoordinator(string name = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _coordinators.GetOrAdd(name, n => new Lazy<EphemeralKeyedWorkCoordinator<T, TKey>>(() =>
        {
            if (!_configurations.TryGetValue(n, out var config))
            {
                throw new InvalidOperationException(
                    $"No keyed coordinator configuration found for name '{n}'. " +
                    $"Call AddEphemeralKeyedWorkCoordinator<{typeof(T).Name}, {typeof(TKey).Name}>(\"{n}\", ...) during registration.");
            }

            var body = config.BodyFactory!(_serviceProvider);
            return new EphemeralKeyedWorkCoordinator<T, TKey>(config.KeySelector!, body, config.Options);
        })).Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var lazy in _coordinators.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Cancel();
            }
        }
    }
}

/// <summary>
/// DI extensions for registering ephemeral work coordinators as services.
/// </summary>
public static class EphemeralServiceCollectionExtensions
{
    /// <summary>
    /// Registers an EphemeralWorkCoordinator as a singleton service.
    /// The coordinator starts processing immediately and runs for the lifetime of the application.
    /// </summary>
    public static IServiceCollection AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
        services.TryAddSingleton(sp =>
        {
            var body = bodyFactory(sp);
            return new EphemeralWorkCoordinator<T>(body, options);
        });

        return services;
    }

    /// <summary>
    /// Registers an EphemeralWorkCoordinator as a singleton with a simpler body (no service provider needed).
    /// </summary>
    public static IServiceCollection AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator<T>(_ => body, options);
    }

    /// <summary>
    /// Registers an EphemeralKeyedWorkCoordinator as a singleton service.
    /// </summary>
    public static IServiceCollection AddEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        services.TryAddSingleton(sp =>
        {
            var body = bodyFactory(sp);
            return new EphemeralKeyedWorkCoordinator<T, TKey>(keySelector, body, options);
        });

        return services;
    }

    /// <summary>
    /// Registers an EphemeralKeyedWorkCoordinator with a simpler body.
    /// </summary>
    public static IServiceCollection AddEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddEphemeralKeyedWorkCoordinator<T, TKey>(keySelector, _ => body, options);
    }

    /// <summary>
    /// Registers a scoped coordinator factory that creates coordinators per scope.
    /// Each scope gets its own coordinator that is disposed when the scope ends.
    /// </summary>
    public static IServiceCollection AddScopedEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
        services.TryAddScoped(sp =>
        {
            var body = bodyFactory(sp);
            return new EphemeralWorkCoordinator<T>(body, options);
        });

        return services;
    }

    /// <summary>
    /// Registers a scoped keyed coordinator factory.
    /// </summary>
    public static IServiceCollection AddScopedEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        services.TryAddScoped(sp =>
        {
            var body = bodyFactory(sp);
            return new EphemeralKeyedWorkCoordinator<T, TKey>(keySelector, body, options);
        });

        return services;
    }

    // ========================================================================
    // NAMED/TYPED COORDINATOR FACTORY PATTERN (like AddHttpClient)
    // ========================================================================

    /// <summary>
    /// Registers a named ephemeral work coordinator using the factory pattern.
    /// Similar to AddHttpClient - allows multiple named configurations that share a factory.
    /// </summary>
    /// <example>
    /// // Register named coordinators
    /// services.AddEphemeralWorkCoordinator&lt;TranslationRequest&gt;("fast",
    ///     sp =&gt; async (req, ct) =&gt; await TranslateAsync(req, ct),
    ///     new EphemeralOptions { MaxConcurrency = 32 });
    ///
    /// services.AddEphemeralWorkCoordinator&lt;TranslationRequest&gt;("slow",
    ///     sp =&gt; async (req, ct) =&gt; await TranslateSlowAsync(req, ct),
    ///     new EphemeralOptions { MaxConcurrency = 4 });
    ///
    /// // Inject the factory
    /// public class MyService
    /// {
    ///     private readonly EphemeralWorkCoordinator&lt;TranslationRequest&gt; _fast;
    ///     private readonly EphemeralWorkCoordinator&lt;TranslationRequest&gt; _slow;
    ///
    ///     public MyService(IEphemeralCoordinatorFactory&lt;TranslationRequest&gt; factory)
    ///     {
    ///         _fast = factory.CreateCoordinator("fast");
    ///         _slow = factory.CreateCoordinator("slow");
    ///     }
    /// }
    /// </example>
    public static IEphemeralCoordinatorBuilder<T> AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
        // Ensure the configuration dictionary exists
        var configKey = typeof(ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>>);
        var configurations = services
            .Where(sd => sd.ServiceType == configKey)
            .Select(sd => sd.ImplementationInstance)
            .OfType<ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>>>()
            .FirstOrDefault();

        if (configurations is null)
        {
            configurations = new ConcurrentDictionary<string, EphemeralCoordinatorConfiguration<T>>();
            services.AddSingleton(configurations);

            // Register the factory as singleton
            services.AddSingleton<IEphemeralCoordinatorFactory<T>>(sp =>
                new EphemeralCoordinatorFactory<T>(sp, configurations));
        }

        // Add or update the configuration for this name
        configurations[name] = new EphemeralCoordinatorConfiguration<T>
        {
            BodyFactory = bodyFactory,
            Options = options
        };

        return new EphemeralCoordinatorBuilder<T>(name, services);
    }

    /// <summary>
    /// Registers a named ephemeral work coordinator with a simpler body (no IServiceProvider needed).
    /// </summary>
    public static IEphemeralCoordinatorBuilder<T> AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        string name,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator<T>(name, _ => body, options);
    }

    /// <summary>
    /// Registers a named keyed ephemeral work coordinator using the factory pattern.
    /// </summary>
    public static IServiceCollection AddEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        string name,
        Func<T, TKey> keySelector,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        var configKey = typeof(ConcurrentDictionary<string, EphemeralKeyedCoordinatorConfiguration<T, TKey>>);
        var configurations = services
            .Where(sd => sd.ServiceType == configKey)
            .Select(sd => sd.ImplementationInstance)
            .OfType<ConcurrentDictionary<string, EphemeralKeyedCoordinatorConfiguration<T, TKey>>>()
            .FirstOrDefault();

        if (configurations is null)
        {
            configurations = new ConcurrentDictionary<string, EphemeralKeyedCoordinatorConfiguration<T, TKey>>();
            services.AddSingleton(configurations);

            services.AddSingleton<IEphemeralKeyedCoordinatorFactory<T, TKey>>(sp =>
                new EphemeralKeyedCoordinatorFactory<T, TKey>(sp, configurations));
        }

        configurations[name] = new EphemeralKeyedCoordinatorConfiguration<T, TKey>
        {
            KeySelector = keySelector,
            BodyFactory = bodyFactory,
            Options = options
        };

        return services;
    }

    /// <summary>
    /// Registers a named keyed ephemeral work coordinator with a simpler body.
    /// </summary>
    public static IServiceCollection AddEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        string name,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddEphemeralKeyedWorkCoordinator<T, TKey>(name, keySelector, _ => body, options);
    }
}
