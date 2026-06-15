using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>
/// Isolates the per-width delta of the reconciliation XOR fold by running the same in-place
/// <see cref="ReconciliationXorScalarBackend.Fold"/> shape over each backend and the dispatching facade. The
/// scalar method is the baseline; every other method measures the speedup the corresponding vector width
/// contributes on this host.
/// </summary>
/// <remarks>
/// A backend whose tier is not hardware-accelerated on the running host throws
/// <see cref="NotSupportedException"/> from its benchmark method, gated by a flag captured in
/// <see cref="Setup"/>; the switcher then reports that method as NA rather than running an unsupported path.
/// Buffers are filled once in <see cref="Setup"/> with the pinned linear-congruential sequence so the work is
/// deterministic across runs and hosts.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[HardwareCounters(HardwareCounter.CacheMisses, HardwareCounter.BranchInstructions, HardwareCounter.BranchMispredictions, HardwareCounter.LlcMisses, HardwareCounter.InstructionRetired)]
public class ReconciliationXorBenchmarks
{
    private byte[] Destination { get; set; } = [];
    private byte[] Source { get; set; } = [];
    private bool Vector128Available { get; set; }
    private bool Vector256Available { get; set; }
    private bool Vector512Available { get; set; }


    /// <summary>The buffer length in bytes the fold runs over, swept across the small-to-page-sized range.</summary>
    [Params(8, 32, 64, 257, 4096)]
    public int Length { get; set; }


    /// <summary>Allocates and fills both buffers once and captures the per-tier support flags.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Destination = new byte[Length];
        Source = new byte[Length];
        Fill(Destination, 1);
        Fill(Source, 2);

        Vector128Available = ReconciliationXorVector128Backend.IsSupported;
        Vector256Available = ReconciliationXorVector256Backend.IsSupported;
        Vector512Available = ReconciliationXorVector512Backend.IsSupported;
    }


    /// <summary>Folds the source into the destination through the scalar reference backend.</summary>
    [Benchmark(Baseline = true)]
    public void FoldScalar()
    {
        ReconciliationXorScalarBackend.Fold(Destination, Source);
    }


    /// <summary>Folds the source into the destination through the 128-bit backend.</summary>
    [Benchmark]
    public void FoldVector128()
    {
        if(!Vector128Available)
        {
            throw new NotSupportedException("The 128-bit vector backend is not supported on this host.");
        }

        ReconciliationXorVector128Backend.Fold(Destination, Source);
    }


    /// <summary>Folds the source into the destination through the 256-bit backend.</summary>
    [Benchmark]
    public void FoldVector256()
    {
        if(!Vector256Available)
        {
            throw new NotSupportedException("The 256-bit vector backend is not supported on this host.");
        }

        ReconciliationXorVector256Backend.Fold(Destination, Source);
    }


    /// <summary>Folds the source into the destination through the 512-bit backend.</summary>
    [Benchmark]
    public void FoldVector512()
    {
        if(!Vector512Available)
        {
            throw new NotSupportedException("The 512-bit vector backend is not supported on this host.");
        }

        ReconciliationXorVector512Backend.Fold(Destination, Source);
    }


    /// <summary>Folds the source into the destination through the dispatching facade.</summary>
    [Benchmark]
    public void FoldDispatched()
    {
        ReconciliationXor.Fold(Destination, Source);
    }


    private static void Fill(byte[] bytes, ulong seed)
    {
        ulong state = seed;
        for(int i = 0; i < bytes.Length; i++)
        {
            state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
            bytes[i] = (byte)(state >> 56);
        }
    }
}
