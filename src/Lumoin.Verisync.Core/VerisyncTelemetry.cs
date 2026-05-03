namespace Lumoin.Verisync.Core;

/// <summary>
/// Centralised string constants for OpenTelemetry metric, activity, and tag names used across
/// Lumoin.Verisync components.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="VerisyncMetrics"/> holds the <c>Meter</c> instrument instances, this class
/// centralises their names alongside the activity names and activity-tag names that leaf types
/// stamp onto OTel spans. The shape follows OTel naming conventions: lowercase, dot-separated,
/// namespaced under <c>verisync.</c>.
/// </para>
/// </remarks>
public static class VerisyncTelemetry
{
    /// <summary>Meter name for the library. Matches <see cref="VerisyncActivitySource.Name"/>.</summary>
    public const string MeterName = "Lumoin.Verisync";


    /// <summary>Metric name for the count of memory rentals taken from a pool.</summary>
    public const string MemoryRented = "verisync.memory.rented";

    /// <summary>Metric name for the count of memory rentals returned to a pool.</summary>
    public const string MemoryReturned = "verisync.memory.returned";

    /// <summary>Metric name for the current number of active (rented and not yet returned) memory rentals.</summary>
    public const string MemoryActiveRentals = "verisync.memory.active_rentals";

    /// <summary>Metric name for the distribution of allocated buffer sizes in bytes.</summary>
    public const string MemoryAllocatedBytes = "verisync.memory.allocated_bytes";

    /// <summary>Metric name for the distribution of pool rental durations in milliseconds.</summary>
    public const string MemoryRentalDurationMs = "verisync.memory.rental_duration_ms";

    /// <summary>Metric name for the distribution of tagged-memory lifetimes in milliseconds.</summary>
    public const string MemoryLifetimeMs = "verisync.memory.lifetime_ms";


    /// <summary>Activity tag name for the size of a tagged buffer in bytes.</summary>
    public const string TagBufferSize = "verisync.buffer.size";

    /// <summary>Activity tag name for the <see cref="VerisyncKind"/> of a tagged buffer.</summary>
    public const string TagKind = "verisync.kind";

    /// <summary>Activity tag name for the lifetime of a value in milliseconds, set when the lifetime span is stopped.</summary>
    public const string ActivityLifetimeMs = "verisync.lifetime_ms";


    /// <summary>Activity name for the lifetime span of a tagged-memory instance.</summary>
    public const string ActivityNameMemoryLifetime = "verisync.memory.lifetime";

    /// <summary>Activity name for the rental span of a pool-backed buffer.</summary>
    public const string ActivityNamePoolRental = "verisync.memory.pool.rental";
}
