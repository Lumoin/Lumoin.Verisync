using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>
/// A long-running, seed-pinned soak that establishes the throughput and allocation baseline the reconciliation
/// streaming hot path runs at today. The ladder pass reconciles a fixed corpus across a spread of difference
/// sizes and reports symbols per second, wire throughput, and — the figure that motivates a flat cell buffer —
/// bytes allocated per coded symbol. The churn pass runs many short sessions back to back and tracks bytes
/// allocated and live heap per batch, so a future pooled cell buffer can be measured against a constant
/// per-session, flat-heap baseline rather than a recollection.
/// </summary>
/// <remarks>
/// Items are built from the pinned linear-congruential sequence over disjoint counter ranges, so every item is
/// distinct by construction and the whole soak is deterministic. Allocation is read through
/// <see cref="GC.GetTotalAllocatedBytes(bool)"/> deltas around a region whose corpus is pre-built outside the
/// window, so the measured bytes are the kernel's (per-add copies, walk cursors, cell buffers, produced
/// symbols, decoded items) and not the synthetic input. Live heap is read through
/// <see cref="GC.GetTotalMemory(bool)"/>. Output is line-oriented for hand-collation; no files are written.
/// This is a profiling driver, never a CI law.
/// </remarks>
internal static class ReconciliationSoak
{
    private const int ItemWidth = 32;
    private const int ChecksumWidth = 8;

    private const int LadderCorpus = 4096;
    private const int LadderTrials = 3;

    private const int ChurnCorpus = 256;
    private const int ChurnDifference = 16;
    private const int ChurnSessions = 2000;
    private const int ChurnBatch = 100;

    private static readonly int[] LadderDifferences = [1, 16, 256, 4096, 16384];


    /// <summary>Runs the throughput ladder then the churn steady-state pass against the structural well-known contract.</summary>
    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Developer-facing soak driver; the rental-meter line is a fixed diagnostic, never localized end-user text.")]
    public static void Run()
    {
        var contract = new ReconciliationContract(ReconciliationItemDomain.Structural, ItemWidth, ChecksumWidth, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

        //Attach a leak meter over the pool's rent and return counters for the whole run. The ladder leaves the
        //cell buffer on the heap fallback (no pool), so its rentals stay zero; once the churn pass constructs
        //encoders and decoders on the shared pool and disposes them, the rented-minus-returned figure proves
        //zero leaks. The pool's active-rentals gauge is pull-based and force-zeroed on its own disposal, so the
        //emitted rent and return counters are the leak signal.
        long rented = 0;
        long returned = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if(instrument.Meter.Name == BaseMemoryPoolMetrics.MeterName
                && (instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolRentOperationsTotal
                    || instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolReturnOperationsTotal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if(instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolRentOperationsTotal)
            {
                Interlocked.Add(ref rented, measurement);
            }
            else if(instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolReturnOperationsTotal)
            {
                Interlocked.Add(ref returned, measurement);
            }
        });
        listener.Start();

        //Warm the JIT and first-touch paths so the measured runs reflect steady state, not first-call cost.
        Reconcile(contract, LadderCorpus, 8, 8, seed: 0, measureAllocation: false, pool: null, out _, out _);

        RunThroughputLadder(contract);
        RunChurnSteadyState(contract);

        listener.Dispose();
        long netActive = Volatile.Read(ref rented) - Volatile.Read(ref returned);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-rentals] rented={Volatile.Read(ref rented)} returned={Volatile.Read(ref returned)} net-active={netActive}"));
    }


    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Developer-facing soak driver; the headers are fixed diagnostics, never localized end-user text.")]
    private static void RunThroughputLadder(ReconciliationContract contract)
    {
        int symbolBytes = ItemWidth + ChecksumWidth;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-ladder] item {ItemWidth}B + checksum {ChecksumWidth}B, corpus {LadderCorpus:N0}, mean of {LadderTrials} trials"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-ladder]   {"d",8} {"symbols",10} {"ms",9} {"sym/s",13} {"wire MB/s",11} {"alloc KB",11} {"alloc B/sym",12}"));

        foreach(int d in LadderDifferences)
        {
            int leftOnly = (d + 1) / 2;
            int rightOnly = d / 2;

            long totalSymbols = 0;
            double totalMs = 0;
            long totalAllocated = 0;
            for(int trial = 0; trial < LadderTrials; trial++)
            {
                int seed = (1000 * d) + trial;
                long allocated = Reconcile(contract, LadderCorpus, leftOnly, rightOnly, seed, measureAllocation: true, pool: null, out int symbols, out double ms);
                totalMs += ms;
                totalSymbols += symbols;
                totalAllocated += allocated;
            }

            double meanSymbols = (double)totalSymbols / LadderTrials;
            double meanMs = totalMs / LadderTrials;
            double meanAllocated = (double)totalAllocated / LadderTrials;
            double symbolsPerSecond = meanSymbols / (meanMs / 1000.0);
            double wireMbPerSecond = meanSymbols * symbolBytes / (meanMs / 1000.0) / (1024.0 * 1024.0);
            double allocatedPerSymbol = meanAllocated / Math.Max(meanSymbols, 1);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-ladder]   {d,8} {meanSymbols,10:F0} {meanMs,9:F2} {symbolsPerSecond,13:F0} {wireMbPerSecond,11:F1} {meanAllocated / 1024.0,11:F1} {allocatedPerSymbol,12:F1}"));
        }
    }


    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Developer-facing soak driver; the headers are fixed diagnostics, never localized end-user text.")]
    private static void RunChurnSteadyState(ReconciliationContract contract)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-churn] {ChurnSessions:N0} sessions, corpus {ChurnCorpus}, d {ChurnDifference}, batch {ChurnBatch}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-churn]   {"sessions",10} {"alloc total MB",16} {"alloc KB/session",18} {"live heap MB",14}"));

        int leftOnly = (ChurnDifference + 1) / 2;
        int rightOnly = ChurnDifference / 2;

        //The churn pass runs on the shared pool so its rentals flow through the leak meter; disposing every
        //encoder and decoder must drive net-active back to zero by the end of the run.
        BaseMemoryPool pool = BaseMemoryPool.Shared;

        long startAllocated = GC.GetTotalAllocatedBytes(precise: true);
        for(int session = 1; session <= ChurnSessions; session++)
        {
            Reconcile(contract, ChurnCorpus, leftOnly, rightOnly, session, measureAllocation: false, pool, out _, out _);

            if(session % ChurnBatch == 0)
            {
                long allocatedSoFar = GC.GetTotalAllocatedBytes(precise: true) - startAllocated;
                long liveHeap = GC.GetTotalMemory(forceFullCollection: true);
                double allocatedPerSession = (double)allocatedSoFar / session;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[recon-churn]   {session,10:N0} {allocatedSoFar / (1024.0 * 1024.0),16:F1} {allocatedPerSession / 1024.0,18:F2} {liveHeap / (1024.0 * 1024.0),14:F1}"));
            }
        }
    }


    private static long Reconcile(ReconciliationContract contract, int corpus, int leftOnly, int rightOnly, int seed, bool measureAllocation, MemoryPool<byte>? pool, out int symbols, out double elapsedMs)
    {
        //Disjoint counter ranges: the shared corpus sits low, the one-sided buckets sit far above it and far
        //apart from each other, each shifted per seed, so no item collides within a run regardless of how large
        //the difference is relative to the corpus.
        long seedShift = (long)seed * 1_000_000L;
        long leftBase = 1_000_000_000L + seedShift;
        long rightBase = 5_000_000_000L + seedShift;

        byte[][] shared = new byte[corpus][];
        for(int i = 0; i < corpus; i++)
        {
            shared[i] = BuildItem(i);
        }

        byte[][] left = new byte[leftOnly][];
        for(int i = 0; i < leftOnly; i++)
        {
            left[i] = BuildItem(leftBase + i);
        }

        byte[][] right = new byte[rightOnly][];
        for(int i = 0; i < rightOnly; i++)
        {
            right[i] = BuildItem(rightBase + i);
        }

        //Time and allocation share one fence around the kernel region (encode the sets, then stream and decode
        //to completion); the synthetic item construction above is excluded from both. The timestamp is taken
        //after the allocation snapshot so the snapshot's own cost is not charged to the elapsed time.
        long before = measureAllocation ? GC.GetTotalAllocatedBytes(precise: true) : 0;
        long startTicks = Stopwatch.GetTimestamp();

        //The corpus size is the lower bound on the cells a session touches, so it pre-sizes the cell stores;
        //pool is null on the ladder (heap fallback, an apples-to-apples allocation read) and the shared pool
        //on the churn pass (the tracked, pooled path the leak meter watches).
        int hint = corpus + Math.Max(leftOnly, rightOnly);

        int index;
        using(var leftEncoder = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, pool, hint))
        using(var rightEncoder = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, pool, hint))
        using(var decoder = new ReconciliationDecoder(contract, pool, hint))
        {
            for(int i = 0; i < corpus; i++)
            {
                leftEncoder.Add(shared[i]);
                rightEncoder.Add(shared[i]);
            }

            for(int i = 0; i < leftOnly; i++)
            {
                leftEncoder.Add(left[i]);
            }

            for(int i = 0; i < rightOnly; i++)
            {
                rightEncoder.Add(right[i]);
            }

            index = 0;
            while(true)
            {
                decoder.Absorb(leftEncoder.ProduceNext().Combine(rightEncoder.ProduceNext()));
                index++;
                if(decoder.IsComplete)
                {
                    break;
                }
            }
        }

        elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        long allocated = measureAllocation ? GC.GetTotalAllocatedBytes(precise: true) - before : 0;
        symbols = index;

        return allocated;
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
