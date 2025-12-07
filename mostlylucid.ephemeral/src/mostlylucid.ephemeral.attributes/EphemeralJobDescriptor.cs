using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Attributes;

internal delegate Task JobInvoker(object target, CancellationToken cancellationToken, SignalEvent signal, object? payload);

/// <summary>
/// Metadata for an attributed job method.
/// </summary>
public sealed class EphemeralJobDescriptor
{
    private readonly JobInvoker _invoker;
    private readonly int? _keySourceParamIndex;
    private readonly string? _keySourcePropertyPath;

    internal EphemeralJobDescriptor(object target, MethodInfo method, EphemeralJobAttribute attribute, EphemeralJobsAttribute? classAttribute)
    {
        Target = target;
        Method = method;
        Attribute = attribute;
        ClassAttribute = classAttribute;
        (_invoker, _keySourceParamIndex, _keySourcePropertyPath) = BuildInvoker(method);

        // Compute effective values from class + method attributes
        EffectivePriority = attribute.Priority != 0 ? attribute.Priority : (classAttribute?.DefaultPriority ?? 0);
        EffectiveMaxConcurrency = attribute.MaxConcurrency != 1 ? attribute.MaxConcurrency : (classAttribute?.DefaultMaxConcurrency ?? 1);

        var prefix = classAttribute?.SignalPrefix;
        EffectiveTriggerSignal = !string.IsNullOrEmpty(prefix) ? $"{prefix}.{attribute.TriggerSignal}" : attribute.TriggerSignal;

        // Parse lane (format: "name" or "name:concurrency")
        var effectiveLane = attribute.Lane ?? classAttribute?.DefaultLane;
        (Lane, LaneMaxConcurrency) = ParseLane(effectiveLane);
    }

    public object Target { get; }
    public MethodInfo Method { get; }
    public EphemeralJobAttribute Attribute { get; }
    public EphemeralJobsAttribute? ClassAttribute { get; }

    /// <summary>
    /// Effective priority (merged from class and method attributes).
    /// </summary>
    public int EffectivePriority { get; }

    /// <summary>
    /// Effective max concurrency (merged from class and method attributes).
    /// </summary>
    public int EffectiveMaxConcurrency { get; }

    /// <summary>
    /// Effective trigger signal (with class prefix applied).
    /// </summary>
    public string EffectiveTriggerSignal { get; }

    /// <summary>
    /// Timeout as TimeSpan, or null if not set.
    /// </summary>
    public TimeSpan? Timeout => Attribute.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(Attribute.TimeoutMs) : null;

    /// <summary>
    /// Whether the operation should be pinned (never evicted).
    /// </summary>
    public bool IsPinned => Attribute.Pin;

    /// <summary>
    /// Time after completion when the operation should be evicted.
    /// </summary>
    public TimeSpan? ExpireAfter => Attribute.ExpireAfterMs > 0 ? TimeSpan.FromMilliseconds(Attribute.ExpireAfterMs) : null;

    /// <summary>
    /// Signals to await before starting the job.
    /// </summary>
    public IReadOnlyList<string>? AwaitSignals => Attribute.AwaitSignals;

    /// <summary>
    /// Timeout for awaiting signals.
    /// </summary>
    public TimeSpan? AwaitTimeout => Attribute.AwaitTimeoutMs > 0 ? TimeSpan.FromMilliseconds(Attribute.AwaitTimeoutMs) : null;

    /// <summary>
    /// Processing lane for this job. Jobs in the same lane share concurrency control.
    /// </summary>
    public string Lane { get; }

    /// <summary>
    /// Maximum concurrency for the lane (parsed from Lane attribute).
    /// 0 means use default (sum of job MaxConcurrency values).
    /// </summary>
    public int LaneMaxConcurrency { get; }

    private static (string name, int maxConcurrency) ParseLane(string? lane)
    {
        if (string.IsNullOrEmpty(lane))
            return ("default", 0);

        var colonIndex = lane.IndexOf(':');
        if (colonIndex < 0)
            return (lane, 0);

        var name = lane.Substring(0, colonIndex);
        var concurrencyStr = lane.Substring(colonIndex + 1);
        return int.TryParse(concurrencyStr, out var concurrency)
            ? (name, concurrency)
            : (lane, 0);
    }

    public bool Matches(SignalEvent signal) =>
        StringPatternMatcher.Matches(signal.Signal, EffectiveTriggerSignal);

    /// <summary>
    /// Extract the operation key from the signal and/or payload.
    /// </summary>
    public string? ExtractKey(SignalEvent signal, object? payload)
    {
        // Priority: KeyFromPayload > KeyFromSignal > OperationKey

        if (!string.IsNullOrEmpty(Attribute.KeyFromPayload) && payload != null)
        {
            var value = GetPropertyValue(payload, Attribute.KeyFromPayload!);
            if (value != null)
                return value.ToString();
        }

        if (_keySourceParamIndex.HasValue && payload != null)
        {
            var value = string.IsNullOrEmpty(_keySourcePropertyPath)
                ? payload
                : GetPropertyValue(payload, _keySourcePropertyPath);
            if (value != null)
                return value.ToString();
        }

        if (Attribute.KeyFromSignal)
            return signal.Key;

        return Attribute.OperationKey;
    }

    public Task InvokeAsync(CancellationToken cancellationToken, SignalEvent signal, object? payload = null) =>
        _invoker(Target, cancellationToken, signal, payload);

    private static object? GetPropertyValue(object obj, string propertyPath)
    {
        var parts = propertyPath.Split('.');
        var current = obj;

        foreach (var part in parts)
        {
            if (current == null) return null;

            var type = current.GetType();
            var prop = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                current = prop.GetValue(current);
                continue;
            }

            var field = type.GetField(part, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                current = field.GetValue(current);
                continue;
            }

            return null;
        }

        return current;
    }

    private static (JobInvoker, int?, string?) BuildInvoker(MethodInfo method)
    {
        var parameters = method.GetParameters();
        int? keySourceIndex = null;
        string? keySourcePath = null;

        // Find KeySource parameter
        for (var i = 0; i < parameters.Length; i++)
        {
            var keyAttr = parameters[i].GetCustomAttribute<KeySourceAttribute>();
            if (keyAttr != null)
            {
                keySourceIndex = i;
                keySourcePath = keyAttr.PropertyPath;
                break;
            }
        }

        return ((target, token, signal, payload) =>
        {
            var args = new List<object?>(parameters.Length);
            foreach (var parameter in parameters)
            {
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    args.Add(token);
                    continue;
                }

                if (parameter.ParameterType == typeof(SignalEvent))
                {
                    args.Add(signal);
                    continue;
                }

                // If payload is provided and matches the parameter type, inject it
                if (payload != null && parameter.ParameterType.IsInstanceOfType(payload))
                {
                    args.Add(payload);
                    continue;
                }

                // Check for default value
                if (parameter.HasDefaultValue)
                {
                    args.Add(parameter.DefaultValue);
                    continue;
                }

                throw new InvalidOperationException($"Unsupported parameter '{parameter.Name}' in job '{method.Name}'.");
            }

            var result = method.Invoke(target, args.ToArray());
            if (result is Task task)
                return task;

            if (result is ValueTask valueTask)
                return valueTask.AsTask();

            return Task.CompletedTask;
        }, keySourceIndex, keySourcePath);
    }
}
