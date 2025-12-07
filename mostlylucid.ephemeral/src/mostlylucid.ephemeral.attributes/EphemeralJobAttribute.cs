using System;

namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
/// Marks a method as a signal-driven job.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EphemeralJobAttribute : Attribute
{
    public EphemeralJobAttribute(string triggerSignal)
    {
        TriggerSignal = triggerSignal ?? throw new ArgumentNullException(nameof(triggerSignal));
    }

    /// <summary>
    /// Signal pattern (supports '*' and '?' wildcards) that triggers this job.
    /// </summary>
    public string TriggerSignal { get; }

    /// <summary>
    /// Optional key to tag the operation emitted when the job runs.
    /// </summary>
    public string? OperationKey { get; set; }
}
