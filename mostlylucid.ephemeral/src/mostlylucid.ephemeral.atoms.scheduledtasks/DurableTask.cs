using System.Text.Json;

namespace Mostlylucid.Ephemeral.Atoms.ScheduledTasks;

/// <summary>
///     Represents a durable unit of work that should stay in the window until its responsibility is fulfilled.
/// </summary>
public sealed record DurableTask(
    string Name,
    string Signal,
    DateTimeOffset ScheduledAt,
    string? Key = null,
    JsonElement? Payload = null,
    string? Description = null);