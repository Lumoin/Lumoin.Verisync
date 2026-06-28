using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>
/// Measures end-to-end encoder throughput over the facade the kernel actually folds through. The benchmark
/// adds a deterministically built item set into a fresh encoder and produces a fixed-size symbol prefix; the
/// XOR micro-benchmark isolates the backend-only delta, while this exercise reports the cost the consolidated
/// fold carries inside the real walk-and-fold loop.
/// </summary>
/// <remarks>
/// The item set is built once in <see cref="Setup"/> from the pinned linear-congruential sequence, with the
/// item index mixed into the seed so the items are distinct with overwhelming probability (a duplicate
/// would merely XOR-cancel, which the encoder tolerates). Each iteration builds a new
/// encoder so the produced-cell buffers do not carry across iterations.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[HardwareCounters(HardwareCounter.CacheMisses, HardwareCounter.BranchInstructions, HardwareCounter.BranchMispredictions, HardwareCounter.LlcMisses, HardwareCounter.InstructionRetired)]
public class ReconciliationEncoderBenchmarks
{
    private const int ItemWidth = 32;
    private const int ChecksumWidth = 8;
    private const int SymbolCount = 64;

    private ReconciliationContract Contract { get; } = new(ReconciliationItemDomain.Structural, ItemWidth, ChecksumWidth, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
    private byte[][] Items { get; set; } = [];


    /// <summary>The number of items added to the encoder before the symbol prefix is produced.</summary>
    [Params(1_000, 10_000)]
    public int ItemCount { get; set; }


    /// <summary>Builds the distinct item set once from the pinned sequence.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Items = new byte[ItemCount][];
        for(int item = 0; item < ItemCount; item++)
        {
            var bytes = new byte[ItemWidth];
            ulong state = unchecked(1UL + ((ulong)item * ItemWidth));
            for(int i = 0; i < ItemWidth; i++)
            {
                state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
                bytes[i] = (byte)(state >> 56);
            }

            Items[item] = bytes;
        }
    }


    /// <summary>Adds every item to a fresh encoder and produces the fixed symbol prefix.</summary>
    [Benchmark]
    public ReconciliationSymbol Encode()
    {
        using var encoder = new ReconciliationEncoder(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(byte[] item in Items)
        {
            encoder.Add(item);
        }

        ReconciliationSymbol last = encoder.ProduceNext();
        for(int n = 1; n < SymbolCount; n++)
        {
            last = encoder.ProduceNext();
        }

        return last;
    }
}
