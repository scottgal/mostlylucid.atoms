using BenchmarkDotNet.Running;
using Mostlylucid.Ephemeral.Benchmarks;

// Run cleanup method benchmarks
BenchmarkRunner.Run<CleanupBenchmarks>();
BenchmarkRunner.Run<CleanupScalabilityBenchmarks>();
BenchmarkRunner.Run<CleanupEdgeCaseBenchmarks>();
