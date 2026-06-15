using System;
using BenchmarkDotNet.Running;

namespace Lumoin.Verisync.Benchmarks;

internal static class Program
{
    /// <summary>
    /// Runs benchmarks via BenchmarkDotNet's
    /// <see cref="BenchmarkSwitcher"/>, which discovers every
    /// public type with at least one <c>[Benchmark]</c> method in
    /// the executing assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Common invocations: <c>dotnet run -c Release</c> launches
    /// an interactive selector; <c>dotnet run -c Release -- --filter "*"</c>
    /// runs everything; <c>dotnet run -c Release -- --filter "*MergeBenchmark*"</c>
    /// runs one benchmark class.
    /// </para>
    /// <para>
    /// The driver intercepts two custom flags before the switcher:
    /// <c>--reconciliation-overhead</c> runs <see cref="ReconciliationOverheadReport.Run"/>, a seed-pinned
    /// wire-cost measurement; <c>--reconciliation-soak</c> runs <see cref="ReconciliationSoak.Run"/>, a
    /// long-running throughput and allocation baseline. Neither is a timing benchmark. Every other flag after
    /// the bare <c>--</c> is handed to BenchmarkDotNet's own argument parser.
    /// </para>
    /// </remarks>
    public static void Main(string[] args)
    {
        if(Array.Exists(args, argument => string.Equals(argument, "--reconciliation-overhead", StringComparison.Ordinal)))
        {
            ReconciliationOverheadReport.Run();

            return;
        }

        if(Array.Exists(args, argument => string.Equals(argument, "--reconciliation-soak", StringComparison.Ordinal)))
        {
            ReconciliationSoak.Run();

            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
