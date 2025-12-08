using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Cronos;
using Mostlylucid.Ephemeral.Atoms.ScheduledTasks;
using Xunit;

namespace Mostlylucid.Ephemeral.Tests;

public sealed class ScheduledTasksAtomTests
{
    [Fact]
    public async Task CronScheduleEnqueuesDurableTaskAsTimeAdvances()
    {
        var tasks = new List<DurableTask>();
        await using var durable = new DurableTaskAtom(async (task, _) =>
        {
            tasks.Add(task);
            await Task.CompletedTask;
        });

        var now = DateTimeOffset.UtcNow;
        var definition = new ScheduledTaskDefinition(
            name: "heartbeat",
            cronExpressionText: "*/1 * * * *",
            cronExpression: CronExpression.Parse("*/1 * * * *", CronFormat.Standard),
            signal: "schedule.tick",
            key: "heartbeat",
            payload: default,
            description: "heartbeat job",
            timeZone: null,
            cronFormat: CronFormat.Standard,
            runOnStartup: true);

        var options = new ScheduledTasksOptions
        {
            Clock = () => now,
            AutoStart = false
        };

        await using var scheduler = new ScheduledTasksAtom(durable, new[] { definition }, options);

        await scheduler.TriggerAsync();
        await durable.WaitForIdleAsync();
        Assert.Single(tasks);
        Assert.Equal("schedule.tick", tasks[0].Signal);
        Assert.Equal(now, tasks[0].ScheduledAt);

        var attempts = 0;
        while (tasks.Count < 2 && attempts++ < 5)
        {
            now = now.AddMinutes(1);
            await scheduler.TriggerAsync();
            await durable.WaitForIdleAsync();
        }

        Assert.Equal(2, tasks.Count);
        Assert.True(tasks[1].ScheduledAt > tasks[0].ScheduledAt);
    }

    [Fact]
    public void LoadFromJsonFileParsesDefinitions()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "daily",
                cron = "0 0 * * *",
                signal = "schedule.daily",
                key = "daily",
                description = "Daily signal",
                runOnStartup = true,
                payload = new { type = "daily" }
            }
        });

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var definitions = ScheduledTaskDefinition.LoadFromJsonFile(path);

            Assert.Single(definitions);
            var def = definitions[0];
            Assert.Equal("daily", def.Name);
            Assert.Equal("schedule.daily", def.Signal);
            Assert.True(def.RunOnStartup);
            Assert.Equal("daily", def.Key);
            Assert.Equal("Daily signal", def.Description);
            Assert.NotNull(def.Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
