using System;

namespace Mostlylucid.Ephemeral.Atoms.Data;

/// <summary>
/// Configuration for data storage atoms.
/// </summary>
public class DataStorageConfig
{
    /// <summary>
    /// Database/storage name used in signal patterns (e.g., "orders" for "save.data.orders").
    /// </summary>
    public string DatabaseName { get; set; } = "default";

    /// <summary>
    /// Signal pattern prefix for save operations. Default is "save.data".
    /// Full pattern becomes "{SignalPrefix}.{DatabaseName}" (e.g., "save.data.orders").
    /// </summary>
    public string SignalPrefix { get; set; } = "save.data";

    /// <summary>
    /// Signal pattern prefix for load operations. Default is "load.data".
    /// Full pattern becomes "{LoadSignalPrefix}.{DatabaseName}" (e.g., "load.data.orders").
    /// </summary>
    public string LoadSignalPrefix { get; set; } = "load.data";

    /// <summary>
    /// Signal pattern prefix for delete operations. Default is "delete.data".
    /// </summary>
    public string DeleteSignalPrefix { get; set; } = "delete.data";

    /// <summary>
    /// Maximum concurrent write operations. Default is 1 (sequential writes).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Whether to batch writes for efficiency. Default is false.
    /// </summary>
    public bool EnableBatching { get; set; } = false;

    /// <summary>
    /// Batch size when batching is enabled. Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Batch timeout when batching is enabled. Default is 1 second.
    /// </summary>
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether to emit completion signals after operations. Default is true.
    /// </summary>
    public bool EmitCompletionSignals { get; set; } = true;

    /// <summary>
    /// Gets the full save signal pattern.
    /// </summary>
    public string SaveSignalPattern => $"{SignalPrefix}.{DatabaseName}";

    /// <summary>
    /// Gets the full load signal pattern.
    /// </summary>
    public string LoadSignalPattern => $"{LoadSignalPrefix}.{DatabaseName}";

    /// <summary>
    /// Gets the full delete signal pattern.
    /// </summary>
    public string DeleteSignalPattern => $"{DeleteSignalPrefix}.{DatabaseName}";
}

/// <summary>
/// Payload for data storage operations.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of the value.</typeparam>
public record DataPayload<TKey, TValue>(TKey Key, TValue Value);

/// <summary>
/// Payload for data load operations.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
public record LoadPayload<TKey>(TKey Key, TaskCompletionSource<object?> Result);

/// <summary>
/// Payload for data delete operations.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
public record DeletePayload<TKey>(TKey Key);
