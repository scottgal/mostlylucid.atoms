using BenchmarkDotNet.Running;
using Mostlylucid.Ephemeral.Benchmarks;

// Run all benchmarks
BenchmarkRunner.Run<SignalCleanupBenchmarks>();
BenchmarkRunner.Run<SignalLifetimeComparisonBenchmarks>();
BenchmarkRunner.Run<AtomSignalCleanupBenchmarks>();
