using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Factory interface for creating named/typed ephemeral work coordinators.
/// Similar to IHttpClientFactory - each named coordinator shares configuration but has independent state.
/// </summary>
public interface IEphemeralCoordinatorFactory<T>
{
    EphemeralWorkCoordinator<T> CreateCoordinator(string name = "");
}

/// <summary>
/// Factory interface for creating named/typed keyed ephemeral work coordinators.
/// </summary>
public interface IEphemeralKeyedCoordinatorFactory<T, TKey>
    where TKey : notnull
{
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
/// </summary>
public interface IEphemeralCoordinatorBuilder<T>
{
    string Name { get; }
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
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
#endif

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
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
#endif

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

    public static IServiceCollection AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator<T>(_ => body, options);
    }

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

    public static IServiceCollection AddEphemeralKeyedWorkCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddEphemeralKeyedWorkCoordinator<T, TKey>(keySelector, _ => body, options);
    }

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

    public static IEphemeralCoordinatorBuilder<T> AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
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

            services.AddSingleton<IEphemeralCoordinatorFactory<T>>(sp =>
                new EphemeralCoordinatorFactory<T>(sp, configurations));
        }

        configurations[name] = new EphemeralCoordinatorConfiguration<T>
        {
            BodyFactory = bodyFactory,
            Options = options
        };

        return new EphemeralCoordinatorBuilder<T>(name, services);
    }

    public static IEphemeralCoordinatorBuilder<T> AddEphemeralWorkCoordinator<T>(
        this IServiceCollection services,
        string name,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator<T>(name, _ => body, options);
    }

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

    /// <summary>
    /// Alias for <see cref="AddEphemeralWorkCoordinator{T}(IServiceCollection, Func{IServiceProvider, Func{T, CancellationToken, Task}}, EphemeralOptions?)"/> so coordinators read like service registrations.
    /// </summary>
    public static IServiceCollection AddCoordinator<T>(
        this IServiceCollection services,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator(bodyFactory, options);
    }

    /// <summary>
    /// Alias for <see cref="AddEphemeralWorkCoordinator{T}(IServiceCollection, Func{T, CancellationToken, Task}, EphemeralOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddCoordinator<T>(
        this IServiceCollection services,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return services.AddEphemeralWorkCoordinator(body, options);
    }

    /// <summary>
    /// Alias for <see cref="AddScopedEphemeralWorkCoordinator{T}(IServiceCollection, Func{IServiceProvider, Func{T, CancellationToken, Task}}, EphemeralOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddScopedCoordinator<T>(
        this IServiceCollection services,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
    {
        return services.AddScopedEphemeralWorkCoordinator(bodyFactory, options);
    }

    /// <summary>
    /// Alias for <see cref="AddEphemeralKeyedWorkCoordinator{T, TKey}(IServiceCollection, Func{T, TKey}, Func{T, CancellationToken, Task}, EphemeralOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddKeyedCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddEphemeralKeyedWorkCoordinator(keySelector, body, options);
    }

    /// <summary>
    /// Alias for <see cref="AddEphemeralKeyedWorkCoordinator{T, TKey}(IServiceCollection, Func{T, TKey}, Func{IServiceProvider, Func{T, CancellationToken, Task}}, EphemeralOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddKeyedCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddEphemeralKeyedWorkCoordinator(keySelector, bodyFactory, options);
    }

    /// <summary>
    /// Alias for <see cref="AddScopedEphemeralKeyedWorkCoordinator{T, TKey}(IServiceCollection, Func{T, TKey}, Func{IServiceProvider, Func{T, CancellationToken, Task}}, EphemeralOptions?)"/>.
    /// </summary>
    public static IServiceCollection AddScopedKeyedCoordinator<T, TKey>(
        this IServiceCollection services,
        Func<T, TKey> keySelector,
        Func<IServiceProvider, Func<T, CancellationToken, Task>> bodyFactory,
        EphemeralOptions? options = null)
        where TKey : notnull
    {
        return services.AddScopedEphemeralKeyedWorkCoordinator(keySelector, bodyFactory, options);
    }
}
