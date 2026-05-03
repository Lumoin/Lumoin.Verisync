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
    /// BenchmarkDotNet's own argument parser handles every flag
    /// after the bare <c>--</c>, so the entry point itself does
    /// not need to interpret <paramref name="args"/>.
    /// </para>
    /// </remarks>
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
