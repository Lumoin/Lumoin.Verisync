using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>
/// A seed-pinned measurement of how many coded symbols, and so how many wire bytes, the reconciliation kernel
/// spends to recover a difference of a known size. It is a measurement with anchor rows, never a timing
/// benchmark and never a CI law: it absorbs the difference stream until the decoder reports completion and
/// reports the mean cost against the information-theoretic floor and two transfer baselines.
/// </summary>
/// <remarks>
/// Both sides share a fixed corpus and differ by a swept count of one-sided items; the items are built from
/// the pinned linear-congruential sequence over disjoint counter ranges so every item is distinct by
/// construction and the whole run is deterministic. Output is a single markdown table on the console; no files
/// are written.
/// </remarks>
internal static class ReconciliationOverheadReport
{
    private const int ItemWidth = 32;
    private const int ChecksumWidth = 8;
    private const int CorpusSize = 4096;
    private const int Trials = 10;

    private static readonly int[] Differences = [1, 2, 4, 8, 16, 64, 256, 1024];


    /// <summary>
    /// Runs the sweep and prints the overhead table to the console.
    /// </summary>
    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "This is a developer-facing benchmark driver; the markdown header is a fixed diagnostic, never localized end-user text.")]
    public static void Run()
    {
        var contract = new ReconciliationContract(ReconciliationItemDomain.Structural, ItemWidth, ChecksumWidth, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
        int symbolBytes = ItemWidth + ChecksumWidth;

        Console.WriteLine("| d | mean symbols | mean bytes on wire | floor bytes | overhead x | full-state bytes | hash-list bytes |");
        Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach(int d in Differences)
        {
            int leftOnly = (d + 1) / 2;
            int rightOnly = d / 2;

            long totalSymbols = 0;
            for(int trial = 0; trial < Trials; trial++)
            {
                int seed = (1000 * d) + trial;
                totalSymbols += SymbolsToComplete(contract, seed, leftOnly, rightOnly);
            }

            double meanSymbols = (double)totalSymbols / Trials;
            double meanBytes = meanSymbols * symbolBytes;
            long floorBytes = (long)d * ItemWidth;
            double overhead = meanBytes / floorBytes;
            long fullStateBytes = (long)(CorpusSize + leftOnly) * ItemWidth;
            long hashListBytes = (long)(CorpusSize + leftOnly + CorpusSize + rightOnly) * ItemWidth;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {d} | {meanSymbols:F1} | {meanBytes:F0} | {floorBytes} | {overhead:F2} | {fullStateBytes} | {hashListBytes} |"));
        }
    }


    private static int SymbolsToComplete(ReconciliationContract contract, int seed, int leftOnly, int rightOnly)
    {
        long differenceBase = CorpusSize + ((long)seed * CorpusSize);

        using var left = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        using var right = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        //The shared corpus is common to both sides and cancels in the difference stream.
        for(int i = 0; i < CorpusSize; i++)
        {
            byte[] item = BuildItem(i);
            left.Add(item);
            right.Add(item);
        }

        //One-sided items split into disjoint counter ranges so they never collide with the corpus or each other.
        for(int i = 0; i < leftOnly; i++)
        {
            left.Add(BuildItem(differenceBase + i));
        }

        for(int i = 0; i < rightOnly; i++)
        {
            right.Add(BuildItem(differenceBase + (CorpusSize / 2) + i));
        }

        using var decoder = new ReconciliationDecoder(contract, BaseMemoryPool.Shared);
        int index = 0;
        while(true)
        {
            ReconciliationSymbol difference = left.ProduceNext().Combine(right.ProduceNext());
            decoder.Absorb(difference);
            index++;

            if(decoder.IsComplete)
            {
                return index;
            }
        }
    }


    private static byte[] BuildItem(long counter)
    {
        var bytes = new byte[ItemWidth];
        ulong state = unchecked((ulong)counter + 1UL);
        for(int i = 0; i < ItemWidth; i++)
        {
            state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }
}
