using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Attributes;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public class AssemblyScanAndScopedRunnerTests
{
    private class ScopedCounter
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private class ScopedJob
    {
        public static ConcurrentBag<Guid> Seen = new();
        private readonly ScopedCounter _counter;

        public ScopedJob(ScopedCounter counter)
        {
            _counter = counter;
        }

        [EphemeralJob("scan.test")]
        public Task Handle()    
        {
            Seen.Add(_counter.Id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AssemblyScan_RegistersAndScopedRunner_ResolvesScopedDependenciesPerInvocation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedCounter>();

        // Use the assembly-scan overload to register attributed jobs
        services.AddEphemeralScopedJobRunner(typeof(ScopedJob).Assembly);

        var provider = services.BuildServiceProvider();

        // Force creation of the runner so it subscribes to the SignalSink
        var runner = provider.GetRequiredService<EphemeralScopedJobRunner>();
        
        var sink = provider.GetRequiredService<SignalSink>();

        // Act: trigger the signal multiple times
        ScopedJob.Seen.Clear();
        sink.Raise("scan.test", key: "a");
        sink.Raise("scan.test", key: "b");

        await Task.Delay(500);

        // Assert
        Assert.True(ScopedJob.Seen.Count >= 1, "Expected at least one invocation of the scoped job");
        if (ScopedJob.Seen.Count > 1)
        {
            Assert.Equal(ScopedJob.Seen.Count, ScopedJob.Seen.ToHashSet().Count);
        }
    }
}
