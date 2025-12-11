using System.Diagnostics;
using Mostlylucid.Ephemeral;
using Spectre.Console;

namespace Mostlylucid.Ephemeral.Demo;

/// <summary>
/// Demonstrates DependencyCoordinator for topological execution.
///
/// Pattern: Declare dependencies, get automatic ordering and parallel execution.
/// Use case: Database migrations, build pipelines, multi-stage ETL.
/// </summary>
public static class DependencyCoordinatorDemo
{
    public record MigrationTask(string Name, string Sql, TimeSpan Duration);

    public static async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[yellow]═══ Dependency Coordinator Demo ═══[/]\n");
        AnsiConsole.MarkupLine("[grey]Demonstrates topological execution of dependent tasks[/]");
        AnsiConsole.MarkupLine("[grey]Pattern: Automatic dependency ordering + parallel execution[/]\n");

        var sink = new SignalSink(maxCapacity: 500);

        // Track signal events for visualization
        var signalLog = new List<string>();
        using var subscription = sink.Subscribe(signal =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            signalLog.Add($"[{timestamp}] {signal.Signal}");

            var color = signal.Signal switch
            {
                var s when s.Contains(".start") => "cyan",
                var s when s.Contains(".done") => "green",
                var s when s.Contains(".failed") => "red",
                var s when s.Contains(".skipped") => "yellow",
                _ => "grey"
            };

            AnsiConsole.MarkupLine($"[{color}]{signal.Signal}[/] [grey]({signal.Key})[/]");
        });

        // Define migration tasks with dependencies
        var migrations = new Dictionary<string, MigrationTask>
        {
            ["init_schema"] = new("init_schema", "CREATE DATABASE", TimeSpan.FromMilliseconds(100)),
            ["create_users"] = new("create_users", "CREATE TABLE users", TimeSpan.FromMilliseconds(150)),
            ["create_orders"] = new("create_orders", "CREATE TABLE orders", TimeSpan.FromMilliseconds(150)),
            ["create_products"] = new("create_products", "CREATE TABLE products", TimeSpan.FromMilliseconds(150)),
            ["create_user_indexes"] = new("create_user_indexes", "CREATE INDEX", TimeSpan.FromMilliseconds(200)),
            ["create_order_indexes"] = new("create_order_indexes", "CREATE INDEX", TimeSpan.FromMilliseconds(200)),
            ["seed_users"] = new("seed_users", "INSERT users", TimeSpan.FromMilliseconds(300)),
            ["seed_products"] = new("seed_products", "INSERT products", TimeSpan.FromMilliseconds(300)),
            ["seed_orders"] = new("seed_orders", "INSERT orders", TimeSpan.FromMilliseconds(400)),
            ["verify_schema"] = new("verify_schema", "SELECT COUNT(*)", TimeSpan.FromMilliseconds(50)),
        };

        AnsiConsole.MarkupLine("[yellow]Building dependency graph:[/]\n");

        var coordinator = new DependencyCoordinator<MigrationTask>(
            body: ExecuteMigrationAsync,
            maxConcurrency: 3,
            sink: sink);

        // Build complex dependency graph
        coordinator.AddOperation("init_schema", migrations["init_schema"]);

        coordinator.AddOperation("create_users", migrations["create_users"],
            dependsOn: "init_schema");
        coordinator.AddOperation("create_orders", migrations["create_orders"],
            dependsOn: "init_schema");
        coordinator.AddOperation("create_products", migrations["create_products"],
            dependsOn: "init_schema");

        coordinator.AddOperation("create_user_indexes", migrations["create_user_indexes"],
            dependsOn: "create_users");
        coordinator.AddOperation("create_order_indexes", migrations["create_order_indexes"],
            dependsOn: "create_orders");

        coordinator.AddOperation("seed_users", migrations["seed_users"],
            dependsOn: "create_user_indexes");
        coordinator.AddOperation("seed_products", migrations["seed_products"],
            dependsOn: "create_products");
        coordinator.AddOperation("seed_orders", migrations["seed_orders"],
            dependsOn: new[] { "seed_products", "create_order_indexes" });

        coordinator.AddOperation("verify_schema", migrations["verify_schema"],
            dependsOn: new[] { "seed_users", "seed_orders" });

        // Visualize the dependency graph
        DisplayDependencyGraph();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Executing migrations (max 3 concurrent):[/]\n");

        var sw = Stopwatch.StartNew();

        try
        {
            await coordinator.ExecuteAsync();
            sw.Stop();

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]✓ All migrations completed in {sw.ElapsedMilliseconds}ms[/]\n");

            // Show execution order
            DisplayExecutionOrder(signalLog);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Execution failed: {ex.Message}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Key Takeaways:[/]");
        AnsiConsole.MarkupLine("[grey]• Declarative dependencies eliminate manual choreography[/]");
        AnsiConsole.MarkupLine("[grey]• Automatic topological sort ensures correct execution order[/]");
        AnsiConsole.MarkupLine("[grey]• Independent tasks run in parallel (up to MaxConcurrency)[/]");
        AnsiConsole.MarkupLine("[grey]• Failed dependencies automatically skip dependents[/]");
        AnsiConsole.MarkupLine("[grey]• Signal emission for full observability[/]\n");
    }

    private static async Task ExecuteMigrationAsync(MigrationTask migration, CancellationToken ct)
    {
        // Simulate migration execution
        await Task.Delay(migration.Duration, ct);

        // Simulate occasional failures (for demo purposes)
        if (Random.Shared.Next(100) < 2) // 2% failure rate
        {
            throw new Exception($"Migration {migration.Name} failed");
        }
    }

    private static void DisplayDependencyGraph()
    {
        var tree = new Tree("[yellow]Dependency Graph[/]");

        var initNode = tree.AddNode("[cyan]init_schema[/]");

        var usersNode = initNode.AddNode("[cyan]create_users[/]");
        usersNode.AddNode("[cyan]create_user_indexes[/]")
            .AddNode("[cyan]seed_users[/]");

        var ordersNode = initNode.AddNode("[cyan]create_orders[/]");
        ordersNode.AddNode("[cyan]create_order_indexes[/]")
            .AddNode("[cyan]seed_orders[/] [grey](also depends on seed_products)[/]");

        var productsNode = initNode.AddNode("[cyan]create_products[/]");
        productsNode.AddNode("[cyan]seed_products[/]");

        var verifyNode = tree.AddNode("[yellow]verify_schema[/] [grey](depends on seed_users + seed_orders)[/]");

        AnsiConsole.Write(tree);
    }

    private static void DisplayExecutionOrder(List<string> signalLog)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Order")
            .AddColumn("Event")
            .AddColumn("Time");

        var startEvents = signalLog
            .Where(s => s.Contains(".start"))
            .ToList();

        for (int i = 0; i < startEvents.Count; i++)
        {
            var parts = startEvents[i].Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            var time = parts[0].Trim();
            var signal = parts[1].Trim();
            var taskName = signal.Replace("operation.start:", "");

            table.AddRow(
                $"[grey]{i + 1}[/]",
                $"[cyan]{taskName}[/]",
                $"[grey]{time}[/]");
        }

        AnsiConsole.Write(table);
    }
}
