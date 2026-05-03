using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One entry of a serialized counter or clock state: a replica's bytes paired with its counter.
/// </summary>
/// <param name="Replica">The replica's raw identifier bytes.</param>
/// <param name="Count">The replica's counter value.</param>
/// <remarks>
/// The replica is held as raw bytes rather than a live <see cref="ReplicaId"/> so the state record is pure,
/// serialization-trivial data with no owned memory — suitable for persistence in any host store.
/// </remarks>
public sealed record ReplicaCounterEntry(ImmutableArray<byte> Replica, int Count);
