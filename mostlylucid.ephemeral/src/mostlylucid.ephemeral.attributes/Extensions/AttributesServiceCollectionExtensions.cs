using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mostlylucid.Ephemeral.Attributes;

/// <summary>
///     Registers attribute-driven job runners in the DI container.
/// </summary>
public static class AttributesServiceCollectionExtensions
{
    /// <summary>
    ///     Adds an <see cref="EphemeralSignalJobRunner" /> that instantiates the provided attribute job types once.
    /// </summary>
    public static IServiceCollection AddEphemeralSignalJobRunner(
        this IServiceCollection services,
        IEnumerable<Type> jobTypes,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        if (jobTypes is null)
            throw new ArgumentNullException(nameof(jobTypes));

        var jobArray = jobTypes.ToArray();
        if (jobArray.Length == 0)
            throw new ArgumentException("At least one job type must be provided.", nameof(jobTypes));

        services.TryAddSingleton(sp => signalFactory?.Invoke(sp) ?? new SignalSink());

        services.AddSingleton(sp =>
        {
            var sink = sp.GetRequiredService<SignalSink>();
            // Prefer an already-registered service instance (respecting its lifetime) if present.
            // Fallback to ActivatorUtilities.CreateInstance(sp, type) when no registration exists.
            var targets = jobArray.Select(type =>
            {
                var registered = sp.GetService(type);
                return registered ?? ActivatorUtilities.CreateInstance(sp, type);
            }).ToArray();
            return new EphemeralSignalJobRunner(sink, targets, maxJobDuration, options);
        });

        return services;
    }

    /// <summary>
    ///     Adds an <see cref="EphemeralSignalJobRunner" /> for the supplied job types using a default signal sink.
    /// </summary>
    public static IServiceCollection AddEphemeralSignalJobRunner<TJob>(
        this IServiceCollection services,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
        where TJob : class
    {
        return services.AddEphemeralSignalJobRunner(new[] { typeof(TJob) }, maxJobDuration, options, signalFactory);
    }

    /// <summary>
    ///     Adds an <see cref="EphemeralScopedJobRunner" /> that resolves job types per invocation.
    /// </summary>
    public static IServiceCollection AddEphemeralScopedJobRunner(
        this IServiceCollection services,
        IEnumerable<Type> jobTypes,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        if (jobTypes is null)
            throw new ArgumentNullException(nameof(jobTypes));

        var jobArray = jobTypes.ToArray();
        if (jobArray.Length == 0)
            throw new ArgumentException("At least one job type must be provided.", nameof(jobTypes));

        services.TryAddSingleton(sp => signalFactory?.Invoke(sp) ?? new SignalSink());
        services.AddSingleton(sp =>
        {
            var sink = sp.GetRequiredService<SignalSink>();
            return new EphemeralScopedJobRunner(sp, sink, jobArray, maxJobDuration, options);
        });

        return services;
    }

    /// <summary>
    ///     Adds an <see cref="EphemeralScopedJobRunner" /> for the supplied job type.
    /// </summary>
    public static IServiceCollection AddEphemeralScopedJobRunner<TJob>(
        this IServiceCollection services,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
        where TJob : class
    {
        return services.AddEphemeralScopedJobRunner(new[] { typeof(TJob) }, maxJobDuration, options, signalFactory);
    }

    // Assembly-scanning overloads
    public static IServiceCollection AddEphemeralSignalJobRunner(
        this IServiceCollection services,
        Assembly assembly,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        return services.AddEphemeralSignalJobRunner(new[] { assembly }, maxJobDuration, options, signalFactory);
    }

    public static IServiceCollection AddEphemeralSignalJobRunner(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        var types = ScanAssembliesForJobTypes(assemblies).ToArray();
        return services.AddEphemeralSignalJobRunner(types, maxJobDuration, options, signalFactory);
    }

    public static IServiceCollection AddEphemeralScopedJobRunner(
        this IServiceCollection services,
        Assembly assembly,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        return services.AddEphemeralScopedJobRunner(new[] { assembly }, maxJobDuration, options, signalFactory);
    }

    public static IServiceCollection AddEphemeralScopedJobRunner(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        TimeSpan maxJobDuration,
        EphemeralOptions? options = null,
        Func<IServiceProvider, SignalSink>? signalFactory = null)
    {
        var types = ScanAssembliesForJobTypes(assemblies).ToArray();
        return services.AddEphemeralScopedJobRunner(types, maxJobDuration, options, signalFactory);
    }

    // Local helper: replicated from EphemeralScopedJobRunner.ScanAssembliesForJobTypes
    private static IEnumerable<Type> ScanAssembliesForJobTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<EphemeralJobsAttribute>() != null)
            {
                yield return type;
                continue;
            }

            var hasJobMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.GetCustomAttribute<EphemeralJobAttribute>() != null);

            if (hasJobMethods)
                yield return type;
        }
    }
}
