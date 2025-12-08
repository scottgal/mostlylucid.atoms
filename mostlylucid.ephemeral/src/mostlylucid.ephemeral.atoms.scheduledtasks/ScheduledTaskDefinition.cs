using System.Text.Json;
using Cronos;

namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
///     Describes how a scheduled task should fire (cron, signal, payload, etc.).
/// </summary>
public sealed class ScheduledTaskDefinition
{
    public ScheduledTaskDefinition(
        string name,
        string cronExpressionText,
        CronExpression cronExpression,
        string signal,
        string? key,
        JsonElement? payload,
        string? description,
        TimeZoneInfo? timeZone,
        CronFormat cronFormat,
        bool runOnStartup)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        CronExpressionText = cronExpressionText ?? throw new ArgumentNullException(nameof(cronExpressionText));
        CronExpression = cronExpression ?? throw new ArgumentNullException(nameof(cronExpression));
        Signal = signal ?? throw new ArgumentNullException(nameof(signal));
        Key = key;
        Payload = payload;
        Description = description;
        TimeZone = timeZone ?? TimeZoneInfo.Utc;
        CronFormat = cronFormat;
        RunOnStartup = runOnStartup;
    }

    /// <summary>
    ///     Task identifier (for diagnostics/lane names).
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Cron expression that controls when the task runs.
    /// </summary>
    public string CronExpressionText { get; }

    /// <summary>
    ///     Parsed cron expression instance.
    /// </summary>
    public CronExpression CronExpression { get; }

    /// <summary>
    ///     Optional timezone used when evaluating the cron expression.
    /// </summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    ///     Signal to raise when the task executes.
    /// </summary>
    public string Signal { get; }

    /// <summary>
    ///     Optional key associated with the triggered signal.
    /// </summary>
    public string? Key { get; }

    /// <summary>
    ///     Optional payload to include in the durable task.
    /// </summary>
    public JsonElement? Payload { get; }

    /// <summary>
    ///     Optional description for diagnostics.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    ///     Whether the definition should fire immediately when registered (before the first cron occurrence).
    /// </summary>
    public bool RunOnStartup { get; }

    /// <summary>
    ///     Format that was used to parse the cron expression.
    /// </summary>
    public CronFormat CronFormat { get; }

    /// <summary>
    ///     Computes the next occurrence after the provided reference instant (inclusive if requested).
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset reference, bool inclusive = false)
    {
        var baseTime = reference.UtcDateTime;
        var next = CronExpression.GetNextOccurrence(baseTime, TimeZone, inclusive);
        if (next is null)
            return null;

        var utc = DateTime.SpecifyKind(next.Value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    /// <summary>
    ///     Loads definitions from a JSON file.
    /// </summary>
    public static IReadOnlyList<ScheduledTaskDefinition> LoadFromJsonFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Scheduled task config must be an array.");

        var definitions = new List<ScheduledTaskDefinition>();
        foreach (var element in document.RootElement.EnumerateArray()) definitions.Add(Parse(element));

        return definitions;
    }

    /// <summary>
    ///     Parses a single JSON object into a definition.
    /// </summary>
    public static ScheduledTaskDefinition Parse(JsonElement element)
    {
        var name = GetRequiredString(element, "name");
        var cron = GetRequiredString(element, "cron");
        var signal = GetRequiredString(element, "signal");
        var key = element.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
            ? keyValue.GetString()
            : null;
        var description =
            element.TryGetProperty("description", out var descValue) && descValue.ValueKind == JsonValueKind.String
                ? descValue.GetString()
                : null;
        var payload = element.TryGetProperty("payload", out var payloadValue)
            ? payloadValue.Clone()
            : (JsonElement?)null;
        var timezoneId =
            element.TryGetProperty("timeZone", out var tzValue) && tzValue.ValueKind == JsonValueKind.String
                ? tzValue.GetString()
                : null;
        var runOnStartup = element.TryGetProperty("runOnStartup", out var runValue) &&
                           runValue.ValueKind == JsonValueKind.True;
        var format = element.TryGetProperty("format", out var formatValue) &&
                     formatValue.ValueKind == JsonValueKind.String
            ? Enum.Parse<CronFormat>(formatValue.GetString() ?? string.Empty, true)
            : GuessCronFormat(cron);

        CronExpression parsed;
        try
        {
            parsed = CronExpression.Parse(cron, format);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid cron expression '{cron}': {ex.Message}", ex);
        }

        TimeZoneInfo? timeZone = null;
        if (!string.IsNullOrEmpty(timezoneId))
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (TimeZoneNotFoundException ex)
            {
                throw new InvalidOperationException($"Unknown timezone '{timezoneId}' for scheduled task '{name}'.",
                    ex);
            }

        return new ScheduledTaskDefinition(
            name,
            cron,
            parsed,
            signal,
            key,
            payload,
            description,
            timeZone,
            format,
            runOnStartup);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                $"Scheduled task property '{propertyName}' is required and must be a string.");

        return value.GetString()!;
    }

    private static CronFormat GuessCronFormat(string expression)
    {
        var segments = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return segments switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => CronFormat.IncludeSeconds
        };
    }
}