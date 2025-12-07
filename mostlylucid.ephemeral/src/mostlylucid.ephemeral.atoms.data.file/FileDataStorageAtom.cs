using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Data;

namespace Mostlylucid.Ephemeral.Atoms.Data.File;

/// <summary>
/// Configuration specific to file-based storage.
/// </summary>
public class FileDataStorageConfig : DataStorageConfig
{
    /// <summary>
    /// Base directory for storing files. Default is "./data".
    /// </summary>
    public string BasePath { get; set; } = "./data";

    /// <summary>
    /// File extension for stored files. Default is ".json".
    /// </summary>
    public string FileExtension { get; set; } = ".json";

    /// <summary>
    /// JSON serializer options. Default uses indented formatting.
    /// </summary>
    public JsonSerializerOptions? JsonOptions { get; set; }
}

/// <summary>
/// File-based JSON data storage atom.
/// Stores each key-value pair as a separate JSON file.
/// </summary>
/// <typeparam name="TKey">Type of the key (must be convertible to valid filename).</typeparam>
/// <typeparam name="TValue">Type of the value (must be JSON-serializable).</typeparam>
public sealed class FileDataStorageAtom<TKey, TValue> : DataStorageAtomBase<TKey, TValue>
    where TKey : notnull
{
    private readonly FileDataStorageConfig _fileConfig;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _storagePath;

    public FileDataStorageAtom(SignalSink signals, FileDataStorageConfig config)
        : base(signals, config)
    {
        _fileConfig = config;
        _jsonOptions = config.JsonOptions ?? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _storagePath = Path.Combine(_fileConfig.BasePath, _fileConfig.DatabaseName);
        Directory.CreateDirectory(_storagePath);
    }

    /// <summary>
    /// Creates a file storage atom with default configuration.
    /// </summary>
    public FileDataStorageAtom(SignalSink signals, string databaseName, string basePath = "./data")
        : this(signals, new FileDataStorageConfig
        {
            DatabaseName = databaseName,
            BasePath = basePath
        })
    {
    }

    private string GetFilePath(TKey key)
    {
        var fileName = SanitizeFileName(key.ToString() ?? "null");
        return Path.Combine(_storagePath, $"{fileName}{_fileConfig.FileExtension}");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name;
    }

    protected override async Task SaveInternalAsync(TKey key, TValue value, CancellationToken ct)
    {
        var path = GetFilePath(key);
        var json = JsonSerializer.Serialize(value, _jsonOptions);

        // Write to temp file first, then move for atomicity
        var tempPath = path + ".tmp";
        await System.IO.File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);

        // Atomic move (overwrite if exists)
        System.IO.File.Move(tempPath, path, overwrite: true);
    }

    protected override async Task<TValue?> LoadInternalAsync(TKey key, CancellationToken ct)
    {
        var path = GetFilePath(key);
        if (!System.IO.File.Exists(path))
            return default;

        var json = await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TValue>(json, _jsonOptions);
    }

    protected override Task DeleteInternalAsync(TKey key, CancellationToken ct)
    {
        var path = GetFilePath(key);
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
        return Task.CompletedTask;
    }

    protected override Task<bool> ExistsInternalAsync(TKey key, CancellationToken ct)
    {
        var path = GetFilePath(key);
        return Task.FromResult(System.IO.File.Exists(path));
    }

    /// <summary>
    /// Gets the storage directory path.
    /// </summary>
    public string StoragePath => _storagePath;

    /// <summary>
    /// Lists all keys in storage.
    /// </summary>
    public IEnumerable<string> ListKeys()
    {
        if (!Directory.Exists(_storagePath))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_storagePath, $"*{_fileConfig.FileExtension}"))
        {
            yield return Path.GetFileNameWithoutExtension(file);
        }
    }

    /// <summary>
    /// Clears all stored data.
    /// </summary>
    public void Clear()
    {
        if (Directory.Exists(_storagePath))
        {
            foreach (var file in Directory.EnumerateFiles(_storagePath, $"*{_fileConfig.FileExtension}"))
            {
                System.IO.File.Delete(file);
            }
        }
    }
}
