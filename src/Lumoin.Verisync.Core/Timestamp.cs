using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A wall-clock instant expressed as UTC ticks, used to order last-writer-wins updates.
/// </summary>
/// <param name="UtcTicks">The number of 100-nanosecond ticks since the UTC epoch, as produced by <see cref="DateTimeOffset.UtcTicks"/>.</param>
/// <remarks>
/// <para>
/// The library never reads the clock itself. A caller either supplies a <see cref="Timestamp"/>
/// obtained from a <see cref="TimeProvider"/> at a higher level (so one reading can be shared across
/// many writes), or passes a <see cref="TimeProvider"/> to a write that stamps a single value.
/// </para>
/// <para>
/// Last-writer-wins ordering compares timestamps first; ties are broken by the writing
/// <see cref="ReplicaId"/>. Equal <see cref="Timestamp"/> and writer are assumed to identify the same
/// write.
/// </para>
/// </remarks>
[DebuggerDisplay("Timestamp({UtcTicks})")]
public readonly record struct Timestamp(long UtcTicks): IComparable<Timestamp>
{
    /// <inheritdoc/>
    public int CompareTo(Timestamp other) => UtcTicks.CompareTo(other.UtcTicks);

    /// <summary>Determines whether <paramref name="left"/> precedes <paramref name="right"/>.</summary>
    public static bool operator <(Timestamp left, Timestamp right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> precedes or equals <paramref name="right"/>.</summary>
    public static bool operator <=(Timestamp left, Timestamp right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> follows <paramref name="right"/>.</summary>
    public static bool operator >(Timestamp left, Timestamp right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> follows or equals <paramref name="right"/>.</summary>
    public static bool operator >=(Timestamp left, Timestamp right) => left.CompareTo(right) >= 0;
}
