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
}
