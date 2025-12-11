using BenchmarkDotNet.Running;
using Mostlylucid.Ephemeral.Benchmarks;

// Run all benchmarks
BenchmarkRunner.Run<SignalCleanupBenchmarks>();
BenchmarkRunner.Run<SignalLifetimeComparisonBenchmarks>();
BenchmarkRunner.Run<AtomSignalCleanupBenchmarks>();

// Run new cleanup method benchmarks
BenchmarkRunner.Run<CleanupBenchmarks>();
BenchmarkRunner.Run<CleanupScalabilityBenchmarks>();
BenchmarkRunner.Run<CleanupEdgeCaseBenchmarks>();
