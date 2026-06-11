using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A coalesced run of tombstones for one replica in a serialized compactable <see cref="Rga{TValue}"/>: the
/// tombstoned dots <c>(Replica, c)</c> for every counter <c>c</c> in the inclusive range
/// <c>[FromCounter, ToCounter]</c>.
/// </summary>
/// <param name="Replica">The replica's raw identifier bytes.</param>
/// <param name="FromCounter">The first tombstoned counter in the span; at least one.</param>
/// <param name="ToCounter">The last tombstoned counter in the span; at least <see cref="FromCounter"/>.</param>
/// <remarks>
/// The replica is held as raw bytes rather than a live <see cref="ReplicaId"/> so the state record is pure,
/// serialization-trivial data with no owned memory. A removal of a consecutive range of one replica's
/// elements packs into a single span.
/// </remarks>
public sealed record RgaTombstoneSpan(ImmutableArray<byte> Replica, int FromCounter, int ToCounter);
