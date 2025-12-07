using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Data;

/// <summary>
/// Attribute to declare that a job class requires a specific data storage.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresStorageAttribute : Attribute
{
    public RequiresStorageAttribute(string storageName)
    {
        StorageName = storageName ?? throw new ArgumentNullException(nameof(storageName));
    }

    /// <summary>
    /// Name of the storage (matches DataStorageConfig.DatabaseName).
    /// </summary>
    public string StorageName { get; }

    /// <summary>
    /// Storage type hint (file, sqlite, postgres). If null, uses default.
    /// </summary>
    public string? StorageType { get; set; }
}

/// <summary>
/// Registration for a storage atom factory.
/// </summary>
public sealed class StorageRegistration
{
    public string Name { get; init; } = string.Empty;
    public string StorageType { get; init; } = string.Empty;
    public Func<SignalSink, DataStorageConfig, object> Factory { get; init; } = null!;
    public DataStorageConfig? Config { get; init; }
}

/// <summary>
/// Provides scoped access to storage atoms for jobs.
/// Dynamically creates and caches storage instances based on job requirements.
/// </summary>
public sealed class DataStorageScope : IAsyncDisposable
{
    private readonly SignalSink _signals;
    private readonly ConcurrentDictionary<string, object> _storages = new();
    private readonly Dictionary<string, StorageRegistration> _registrations = new();
    private readonly string _defaultStorageType;

    public DataStorageScope(SignalSink signals, string defaultStorageType = "file")
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _defaultStorageType = defaultStorageType;
    }

    /// <summary>
    /// Registers a storage factory for a specific name and type.
    /// </summary>
    public DataStorageScope Register(StorageRegistration registration)
    {
        var key = $"{registration.Name}:{registration.StorageType}";
        _registrations[key] = registration;
        return this;
    }

    /// <summary>
    /// Registers a storage factory using fluent syntax.
    /// </summary>
    public DataStorageScope Register(
        string name,
        string storageType,
        Func<SignalSink, DataStorageConfig, object> factory,
        DataStorageConfig? config = null)
    {
        return Register(new StorageRegistration
        {
            Name = name,
            StorageType = storageType,
            Factory = factory,
            Config = config
        });
    }

    /// <summary>
    /// Gets or creates a storage atom for the given name.
    /// </summary>
    public TStorage GetStorage<TStorage>(string name, string? storageType = null)
        where TStorage : class
    {
        var type = storageType ?? _defaultStorageType;
        var key = $"{name}:{type}";

        object? storage = null;
        try
        {
            storage = _storages.GetOrAdd(key, _ => CreateStorage(name, type));
        }
        catch (Exception ex)
        {
            _signals.Raise(typeof(TStorage).Name + ".storage.error:" +ex.HResult);
            throw;
        }

        if (storage is TStorage typed)
            return typed;

        throw new InvalidOperationException(
            $"Storage '{name}' is of type {storage.GetType().Name}, not {typeof(TStorage).Name}");
    }

    /// <summary>
    /// Gets required storages for a job type based on RequiresStorageAttribute.
    /// </summary>
    public IReadOnlyList<object> GetRequiredStorages(Type jobType)
    {
        var storages = new List<object>();

        // Check class-level attributes
        foreach (var attr in jobType.GetCustomAttributes<RequiresStorageAttribute>())
        {
            var storage = GetStorageByAttribute(attr);
            storages.Add(storage);
        }

        // Check method-level attributes
        foreach (var method in jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var attr in method.GetCustomAttributes<RequiresStorageAttribute>())
            {
                var storage = GetStorageByAttribute(attr);
                if (!storages.Contains(storage))
                    storages.Add(storage);
            }
        }

        return storages;
    }

    /// <summary>
    /// Creates a job instance with storage dependencies injected.
    /// </summary>
    public object? CreateJobInstance(Type jobType, IServiceProvider? serviceProvider = null)
    {
        var constructors = jobType.GetConstructors();
        if (constructors.Length == 0)
            return Activator.CreateInstance(jobType);

        // Find best constructor
        var ctor = constructors
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.ParameterType;

            // Check if it's a SignalSink
            if (paramType == typeof(SignalSink))
            {
                args[i] = _signals;
                continue;
            }

            // Check if it's a DataStorageScope
            if (paramType == typeof(DataStorageScope))
            {
                args[i] = this;
                continue;
            }

            // Check if it's an IDataStorageAtom<,>
            if (paramType.IsGenericType)
            {
                var genericDef = paramType.GetGenericTypeDefinition();
                if (genericDef == typeof(IDataStorageAtom<,>))
                {
                    // Try to find matching storage by parameter name or attribute
                    var storageName = GetStorageNameForParameter(param, jobType);
                    if (storageName != null)
                    {
                        var storage = GetStorageByName(storageName, paramType);
                        if (storage != null)
                        {
                            args[i] = storage;
                            continue;
                        }
                    }
                }
            }

            // Try service provider
            if (serviceProvider != null)
            {
                var service = serviceProvider.GetService(paramType);
                if (service != null)
                {
                    args[i] = service;
                    continue;
                }
            }

            // Use default value if available
            if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
                continue;
            }

            throw new InvalidOperationException(
                $"Cannot resolve parameter '{param.Name}' of type {paramType.Name} for job {jobType.Name}");
        }

        return ctor.Invoke(args);
    }

    private object GetStorageByAttribute(RequiresStorageAttribute attr)
    {
        var type = attr.StorageType ?? _defaultStorageType;
        var key = $"{attr.StorageName}:{type}";
        return _storages.GetOrAdd(key, _ => CreateStorage(attr.StorageName, type));
    }

    private object? GetStorageByName(string name, Type requestedType)
    {
        // Try each registered type for this name
        foreach (var reg in _registrations.Values.Where(r => r.Name == name))
        {
            var key = $"{reg.Name}:{reg.StorageType}";
            if (_storages.TryGetValue(key, out var existing) && requestedType.IsInstanceOfType(existing))
                return existing;

            var storage = _storages.GetOrAdd(key, _ => CreateStorage(name, reg.StorageType));
            if (requestedType.IsInstanceOfType(storage))
                return storage;
        }

        // Try default type
        var defaultKey = $"{name}:{_defaultStorageType}";
        if (_storages.TryGetValue(defaultKey, out var defaultStorage) && requestedType.IsInstanceOfType(defaultStorage))
            return defaultStorage;

        return null;
    }

    private string? GetStorageNameForParameter(ParameterInfo param, Type jobType)
    {
        // Check parameter-level attribute (via custom attributes on the parameter)
        // For now, use naming convention: parameter named "ordersStorage" -> "orders"
        var name = param.Name;
        if (name != null && name.EndsWith("Storage", StringComparison.OrdinalIgnoreCase))
            return name.Substring(0, name.Length - 7).ToLowerInvariant();

        // Check class-level RequiresStorage for single storage
        var classAttrs = jobType.GetCustomAttributes<RequiresStorageAttribute>().ToList();
        if (classAttrs.Count == 1)
            return classAttrs[0].StorageName;

        return null;
    }

    private object CreateStorage(string name, string storageType)
    {
        var key = $"{name}:{storageType}";

        if (!_registrations.TryGetValue(key, out var registration))
        {
            // Try name-only match
            registration = _registrations.Values.FirstOrDefault(r => r.Name == name);
        }

        if (registration == null)
        {
            throw new InvalidOperationException(
                $"No storage registered for '{name}' with type '{storageType}'. " +
                $"Register it using DataStorageScope.Register().");
        }

        var config = registration.Config ?? new DataStorageConfig { DatabaseName = name };
        return registration.Factory(_signals, config);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var storage in _storages.Values)
        {
            if (storage is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (storage is IDisposable disposable)
                disposable.Dispose();
        }

        _storages.Clear();
    }
}
