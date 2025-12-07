using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Attributes;

internal delegate Task JobInvoker(object target, CancellationToken cancellationToken, SignalEvent signal);

/// <summary>
/// Metadata for an attributed job method.
/// </summary>
public sealed class EphemeralJobDescriptor
{
    private readonly JobInvoker _invoker;

    internal EphemeralJobDescriptor(object target, MethodInfo method, EphemeralJobAttribute attribute)
    {
        Target = target;
        Method = method;
        Attribute = attribute;
        _invoker = BuildInvoker(method);
    }

    public object Target { get; }
    public MethodInfo Method { get; }
    public EphemeralJobAttribute Attribute { get; }

    public bool Matches(SignalEvent signal) =>
        StringPatternMatcher.Matches(signal.Signal, Attribute.TriggerSignal);

    public Task InvokeAsync(CancellationToken cancellationToken, SignalEvent signal) =>
        _invoker(Target, cancellationToken, signal);

    private static JobInvoker BuildInvoker(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var expectsCancellation = parameters.Any(p => p.ParameterType == typeof(CancellationToken));
        var expectsSignal = parameters.Any(p => p.ParameterType == typeof(SignalEvent));
        if (parameters.Length > (expectsCancellation ? 1 : 0) + (expectsSignal ? 1 : 0))
            throw new InvalidOperationException($"Job method '{method.Name}' has unsupported parameter list.");

        bool IsSignalParameter(ParameterInfo parameter) => parameter.ParameterType == typeof(SignalEvent);
        bool IsCancellationParameter(ParameterInfo parameter) => parameter.ParameterType == typeof(CancellationToken);

        return (target, token, signal) =>
        {
            var args = new List<object?>(parameters.Length);
            foreach (var parameter in parameters)
            {
                if (IsCancellationParameter(parameter))
                {
                    args.Add(token);
                    continue;
                }

                if (IsSignalParameter(parameter))
                {
                    args.Add(signal);
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
        };
    }
}
