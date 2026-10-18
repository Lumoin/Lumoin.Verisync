using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CommandLine;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>Measures encoder retention and process peaks for one configuration per process.</summary>
internal static class ReconciliationScaleProbe
{
    /// <summary>Parses the scale mode options and runs one measurement.</summary>
    public static void Run(string[] args)
    {
        using var parser = new Parser(settings => settings.HelpWriter = Console.Error);
        parser.ParseArguments<Options>(args)
            .WithParsed(Measure)
            .WithNotParsed(_ => Environment.ExitCode = 2);
    }

    /// <summary>Measures all phases while keeping the encoder and pool alive through disposal sampling.</summary>
    [SuppressMessage("Globalization", "CA1303", Justification = "Developer-facing probe diagnostics are invariant and not localized.")]
    private static void Measure(Options options)
    {
        if(options.Items is < 0 or > 150_000_000)
        {
            Console.Error.WriteLine("Refused: --items must be between 0 and 150000000; the upper bound protects this 127 GiB machine from excessive allocation.");
            Environment.ExitCode = 2;

            return;
        }

        if(options.Pool is not ("default" or "heap" or "single") || options.Hint is not ("zero" or "items" or "both")
            || options.Encoder is not ("incremental" or "snapshot") || (options.Encoder == "snapshot" && options.Symbols < 1)
            || options.Symbols < 0 || options.ItemWidth is < 16 or > 1024 || options.ChecksumWidth is < 4 or > 8)
        {
            Console.Error.WriteLine("Refused: use encoder incremental|snapshot, pool default|heap|single, hint zero|items|both, nonnegative symbols (positive for snapshot), item width 16..1024, and checksum width 4..8.");
            Environment.ExitCode = 2;

            return;
        }

        using var meter = new Meter("Lumoin.Verisync.Benchmarks.Scale");
        using MemoryPool<byte> pool = options.Pool switch
        {
            "heap" => new ReconciliationHeapPool(),
            "single" => new BaseMemoryPool(meter, capacityStrategy: _ => 1),
            _ => new BaseMemoryPool(meter),
        };
        var contract = new ReconciliationContract(ReconciliationItemDomain.Structural, options.ItemWidth,
            options.ChecksumWidth, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
        using Process process = Process.GetCurrentProcess();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Record(values, "items", options.Items);
        Record(values, "encoder", options.Encoder);
        Record(values, "pool", options.Pool);
        Record(values, "hint", options.Hint);
        Record(values, "symbols", options.Symbols);
        Record(values, "item_width", options.ItemWidth);
        Record(values, "checksum_width", options.ChecksumWidth);
        Record(values, "status", "completed");
        Record(values, "failure_phase", "");
        Record(values, "exception_type", "");
        Record(values, "exception_message", "");
        Record(values, "failed_add_index_zero_based", "");
        Record(values, "failed_add_ordinal", "");
        Snapshot(values, "before", process);

        MeasureEncoder(options, pool, contract, process, values);

        //The separate frame has returned, so no encoder local can remain a collection root.
        Snapshot(values, "after_drop", process);
        GC.KeepAlive(pool);
        Console.WriteLine(string.Join(',', values.Keys));
        Console.WriteLine(string.Join(',', values.Values.Select(CsvEscape)));
    }

    /// <summary>Seeds or constructs the selected encoder, produces its symbols, and samples disposal while it remains referenced.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MeasureEncoder(Options options, MemoryPool<byte> pool, ReconciliationContract contract,
        Process process, Dictionary<string, string> values)
    {
        ReconciliationEncoder? encoder = null;
        ReconciliationSnapshotEncoder? snapshotEncoder = null;
        long seeded = 0;
        int produced = 0;
        double seedSeconds = 0;
        double produceSeconds = 0;
        try
        {
            try
            {
                //The item limit is checked before narrowing the capacity hint.
                ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Items, Array.MaxLength);
                if(options.Encoder == "snapshot")
                {
                    long start = Stopwatch.GetTimestamp();
                    snapshotEncoder = new ReconciliationSnapshotEncoder(contract,
                        (visit, cancellationToken) => EnumerateItems(options, visit, cancellationToken), pool, options.Symbols);
                    seedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    seeded = options.Items;
                }
                else
                {
                    encoder = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, pool,
                        options.Hint switch
                        {
                            "items" => new ReconciliationCapacityHint(items: (int)options.Items, cells: options.Symbols),
                            "both" => new ReconciliationCapacityHint(items: (int)options.Items, cells: (int)options.Items),
                            _ => ReconciliationCapacityHint.None,
                        });
                }
            }
            catch(Exception exception)
            {
                Failure(values, "construct", exception);
            }

            if(encoder is not null)
            {
                Span<byte> item = stackalloc byte[options.ItemWidth];
                item.Clear();
                long start = Stopwatch.GetTimestamp();
                try
                {
                    for(; seeded < options.Items; seeded++)
                    {
                        uint identifier = (uint)seeded;
                        BinaryPrimitives.WriteUInt32LittleEndian(item, identifier);
                        BinaryPrimitives.WriteUInt32LittleEndian(item[4..], identifier + 1);
                        BinaryPrimitives.WriteUInt32LittleEndian(item[8..], unchecked(identifier * 2654435761U));
                        encoder.Add(item);
                    }
                }
                catch(Exception exception)
                {
                    seedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    Failure(values, "add", exception);
                    Record(values, "failed_add_index_zero_based", seeded);
                    Record(values, "failed_add_ordinal", seeded + 1);
                }
                finally
                {
                    if(seedSeconds == 0)
                    {
                        seedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    }
                }
            }

            Record(values, "seeded_items", seeded);
            Record(values, "seed_seconds", seedSeconds);
            Record(values, "items_per_second", encoder is not null && seedSeconds > 0 ? seeded / seedSeconds : 0);
            Snapshot(values, "after_seed", process);

            if(encoder is not null || snapshotEncoder is not null)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    if(snapshotEncoder is not null)
                    {
                        snapshotEncoder.Produce(options.Symbols, CancellationToken.None);
                        produced = snapshotEncoder.ProducedCount;
                    }
                    else if(encoder is not null)
                    {
                        for(; produced < options.Symbols; produced++)
                        {
                            _ = encoder.ProduceNext();
                        }
                    }
                }
                catch(Exception exception)
                {
                    produceSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    //Keep the first failure as the primary refusal and print any subsequent failure.
                    if(values["status"] == "completed")
                    {
                        Failure(values, "produce", exception);
                    }
                    else
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"produce_exception: {exception.GetType().FullName}: {exception.Message}"));
                    }
                }
                finally
                {
                    if(produceSeconds == 0)
                    {
                        produceSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    }
                }
            }

            Record(values, "produced_symbols", produced);
            Record(values, "produce_seconds", produceSeconds);
            Record(values, "microseconds_per_symbol", produced > 0 ? produceSeconds * 1_000_000 / produced : 0);
            Snapshot(values, "after_produce", process);
        }
        finally
        {
            encoder?.Dispose();
            snapshotEncoder?.Dispose();
        }

        //Keep the disposed encoders alive so the after-dispose snapshot measures Dispose itself and not the collection of what each still references.
        Snapshot(values, "after_dispose", process);
        GC.KeepAlive(encoder);
        GC.KeepAlive(snapshotEncoder);
    }

    /// <summary>Regenerates every synthetic item in stable order using one reusable stack buffer.</summary>
    private static void EnumerateItems(Options options, VisitReconciliationItemDelegate visit, CancellationToken cancellationToken)
    {
        Span<byte> item = stackalloc byte[options.ItemWidth];
        item.Clear();
        for(long index = 0; index < options.Items; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint identifier = (uint)index;
            BinaryPrimitives.WriteUInt32LittleEndian(item, identifier);
            BinaryPrimitives.WriteUInt32LittleEndian(item[4..], identifier + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(item[8..], unchecked(identifier * 2654435761U));
            visit(item);
        }
    }

    /// <summary>Records full-collection managed retention, refreshed process memory, and cumulative collection counts.</summary>
    private static void Snapshot(Dictionary<string, string> values, string phase, Process process)
    {
        long managed = GC.GetTotalMemory(forceFullCollection: true);
        process.Refresh();
        Record(values, phase + "_managed_bytes", managed);
        Record(values, phase + "_working_set_bytes", process.WorkingSet64);
        Record(values, phase + "_peak_working_set_bytes", process.PeakWorkingSet64);
        for(int generation = 0; generation <= 2; generation++)
        {
            Record(values, string.Create(CultureInfo.InvariantCulture, $"{phase}_gc{generation}"), GC.CollectionCount(generation));
        }
    }

    /// <summary>Records the first exception without interrupting subsequent measurement phases.</summary>
    private static void Failure(Dictionary<string, string> values, string phase, Exception exception)
    {
        Record(values, "status", "refused");
        Record(values, "failure_phase", phase);
        Record(values, "exception_type", exception.GetType().FullName ?? exception.GetType().Name);
        Record(values, "exception_message", exception.Message);
    }

    /// <summary>Stores an invariant value and prints its labelled representation.</summary>
    private static void Record(Dictionary<string, string> values, string name, object value)
    {
        string formatted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        values[name] = formatted;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {formatted}"));
    }

    /// <summary>Quotes a CSV field and keeps each record on one physical line.</summary>
    private static string CsvEscape(string value)
    {
        string escaped = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal);

        return "\"" + escaped + "\"";
    }

    /// <summary>Defines the command-line options for one scale measurement.</summary>
    private sealed class Options
    {
        /// <summary>Gets or sets the scale mode selector.</summary>
        [Option("reconciliation-scale")]
        public bool Scale { get; set; }

        /// <summary>Gets or sets the number of distinct items to add or enumerate.</summary>
        [Option("items", Required = true)]
        public long Items { get; set; }

        /// <summary>Gets or sets the encoder mode: incremental retains items and cursors; snapshot regenerates items once per produced range.</summary>
        [Option("encoder", Default = "incremental")]
        public string Encoder { get; set; } = "incremental";

        /// <summary>Gets or sets the backing pool variant.</summary>
        [Option("pool", Default = "default")]
        public string Pool { get; set; } = "default";

        /// <summary>Gets or sets the incremental hint mode: zero uses minimum capacities, items sizes items by count and cells by symbols, and both sizes both by item count. Snapshot mode always sizes cells by symbols.</summary>
        [Option("hint", Default = "zero")]
        public string Hint { get; set; } = "zero";

        /// <summary>Gets or sets the number of symbols to produce; snapshot mode requires a positive count and produces it in one pass.</summary>
        [Option("symbols", Default = 2000)]
        public int Symbols { get; set; }

        /// <summary>Gets or sets the checksum width in bytes.</summary>
        [Option("checksum-width", Default = 8)]
        public int ChecksumWidth { get; set; }

        /// <summary>Gets or sets the item width in bytes.</summary>
        [Option("item-width", Default = 16)]
        public int ItemWidth { get; set; }
    }
}
