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
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<EphemeralJobAttribute>();
            if (attribute is null)
                continue;

            if (!typeof(Task).IsAssignableFrom(method.ReturnType) && !typeof(ValueTask).IsAssignableFrom(method.ReturnType))
            {
                throw new InvalidOperationException($"Method '{method.Name}' must return Task or ValueTask when marked with {nameof(EphemeralJobAttribute)}.");
            }

            descriptors.Add(new EphemeralJobDescriptor(target, method, attribute));
        }

        return descriptors;
    }
}
