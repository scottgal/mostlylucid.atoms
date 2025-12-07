using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
/// Helper to discover attributed job methods.
/// </summary>
public static class EphemeralJobScanner
{
    /// <summary>
    /// Enumerates job descriptors for the provided target object.
    /// </summary>
    public static IReadOnlyList<EphemeralJobDescriptor> Scan(object target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        var descriptors = new List<EphemeralJobDescriptor>();
        var targetType = target.GetType();
        var classAttribute = targetType.GetCustomAttribute<EphemeralJobsAttribute>();
        var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<EphemeralJobAttribute>();
            if (attribute is null)
                continue;

            ValidateMethod(method);
            descriptors.Add(new EphemeralJobDescriptor(target, method, attribute, classAttribute));
        }

        return descriptors;
    }

    /// <summary>
    /// Scan multiple targets and return all descriptors sorted by priority.
    /// </summary>
    public static IReadOnlyList<EphemeralJobDescriptor> ScanAll(IEnumerable<object> targets)
    {
        if (targets is null) throw new ArgumentNullException(nameof(targets));

        return targets
            .SelectMany(Scan)
            .OrderBy(d => d.EffectivePriority)
            .ToList();
    }

    /// <summary>
    /// Scan assemblies for types with EphemeralJobsAttribute and create instances.
    /// </summary>
    public static IReadOnlyList<EphemeralJobDescriptor> ScanAssemblies(IEnumerable<Assembly> assemblies, Func<Type, object?> factory)
    {
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        var descriptors = new List<EphemeralJobDescriptor>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<EphemeralJobsAttribute>() == null)
                    continue;

                var instance = factory(type);
                if (instance != null)
                    descriptors.AddRange(Scan(instance));
            }
        }

        return descriptors.OrderBy(d => d.EffectivePriority).ToList();
    }

    private static void ValidateMethod(MethodInfo method)
    {
        if (!typeof(Task).IsAssignableFrom(method.ReturnType) && !typeof(ValueTask).IsAssignableFrom(method.ReturnType))
        {
            throw new InvalidOperationException($"Method '{method.Name}' must return Task or ValueTask when marked with {nameof(EphemeralJobAttribute)}.");
        }

        var parameters = method.GetParameters();
        var validTypes = new[] { typeof(CancellationToken), typeof(SignalEvent) };

        foreach (var param in parameters)
        {
            // Allow CancellationToken, SignalEvent, or any type that could be a payload
            if (param.ParameterType == typeof(CancellationToken))
                continue;
            if (param.ParameterType == typeof(SignalEvent))
                continue;
            if (param.HasDefaultValue)
                continue;

            // Allow any reference type as potential payload
            if (!param.ParameterType.IsValueType || Nullable.GetUnderlyingType(param.ParameterType) != null)
                continue;

            // Allow value types that are common payloads
            if (param.ParameterType.IsPrimitive || param.ParameterType == typeof(Guid) || param.ParameterType == typeof(DateTime))
                continue;

            throw new InvalidOperationException($"Parameter '{param.Name}' in job '{method.Name}' must have a default value or be CancellationToken/SignalEvent/payload type.");
        }
    }
}
