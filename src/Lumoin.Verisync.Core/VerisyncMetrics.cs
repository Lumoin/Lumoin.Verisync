using System.Diagnostics.Metrics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Holds the single <see cref="Meter"/> and the instrument instances used by the library.
/// </summary>
/// <remarks>
/// <para>
/// Instruments emit unconditionally. The cost when no <see cref="MeterListener"/> is subscribed is
/// a single counter update with no allocation, which is cheap enough to be always-on. Instrument
/// names are taken from <see cref="VerisyncTelemetry"/>.
/// </para>
/// <para>
/// Register the meter in application startup to collect these metrics:
/// </para>
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter(VerisyncTelemetry.MeterName)
///         .AddPrometheusExporter());
/// </code>
/// </remarks>
public static class VerisyncMetrics
{
    private static Meter Meter { get; } = new(VerisyncTelemetry.MeterName);

    /// <summary>Histogram for the distribution of allocated buffer sizes in bytes.</summary>
    public static Histogram<long> MemoryAllocatedBytes { get; } = Meter.CreateHistogram<long>(VerisyncTelemetry.MemoryAllocatedBytes, "By");

    /// <summary>Histogram for the distribution of tagged-memory lifetimes in milliseconds.</summary>
    public static Histogram<double> MemoryLifetimeMs { get; } = Meter.CreateHistogram<double>(VerisyncTelemetry.MemoryLifetimeMs, "ms");

    /// <summary>
    /// Counter of versioned-register writes, dimensioned by the status each established and by whether the
    /// decision was taken on the leader's one-round-trip path.
    /// </summary>
    /// <remarks>
    /// The fast-path rate is this counter's own ratio rather than a metric of its own, because a rate computed
    /// here would fix the window it is averaged over and a backend divides better than the library can guess.
    /// </remarks>
    public static Counter<long> ConsensusWrites { get; } = Meter.CreateCounter<long>(VerisyncTelemetry.ConsensusWrites, "{write}");

    /// <summary>Histogram for how many consensus attempts one write spent.</summary>
    public static Histogram<int> ConsensusWriteAttempts { get; } = Meter.CreateHistogram<int>(VerisyncTelemetry.ConsensusWriteAttempts, "{attempt}");

    /// <summary>Gauge for the size of the membership a register's next write runs under.</summary>
    /// <remarks>
    /// Recorded where a register's membership is set or moved rather than observed through a callback. An
    /// observable gauge would have this meter hold a reference to every register that ever existed, and a
    /// lifetime edge is too high a price for a number that changes only when a record is learned.
    /// </remarks>
    public static Gauge<int> ConsensusMembershipSize { get; } = Meter.CreateGauge<int>(VerisyncTelemetry.ConsensusMembershipSize, "{member}");

    /// <summary>Gauge for the quorum the membership implies, which is the membership's own arithmetic.</summary>
    public static Gauge<int> ConsensusMembershipQuorum { get; } = Meter.CreateGauge<int>(VerisyncTelemetry.ConsensusMembershipQuorum, "{member}");

    /// <summary>
    /// Counter of per-member version probes, dimensioned by the member asked and by how it answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member dimension is a replica identity, which is unbounded in principle and bounded in practice by
    /// the membership: three to seven values, stable for the life of a configuration. Per-member
    /// unreachability is the question this instrument exists to answer, and an unlabelled count would answer a
    /// different one that a readiness report already answers better.
    /// </para>
    /// <para>
    /// A silent member and a faulting one are told apart here and nowhere else. A readiness report collapses
    /// them deliberately, because a gate cannot act differently on the two; a human diagnosing can.
    /// </para>
    /// <para>
    /// It counts probes and not cluster state. Readiness is measured only when a caller asks for it, so a
    /// deployment that never reads readiness emits nothing here, and the series says how the probes that were
    /// made answered rather than how the cluster stands.
    /// </para>
    /// </remarks>
    public static Counter<long> ConsensusProbes { get; } = Meter.CreateCounter<long>(VerisyncTelemetry.ConsensusProbes, "{probe}");
}
