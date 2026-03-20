using BenchmarkDotNet.Running;
using HttpClient.IoUring.Benchmarks;

if (args.Length > 0 && args[0] == "--quick")
{
    await QuickBench.RunAsync(args.Skip(1).ToArray());
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(ThroughputBenchmark).Assembly).Run(args);
}
