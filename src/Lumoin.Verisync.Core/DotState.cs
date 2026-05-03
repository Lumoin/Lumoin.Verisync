using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable form of a <see cref="Dot"/>: a replica's raw identifier bytes paired with its counter.
/// </summary>
/// <param name="Replica">The dot replica's raw identifier bytes.</param>
/// <param name="Counter">The dot's counter.</param>
/// <remarks>
/// The replica is held as raw bytes rather than a live <see cref="ReplicaId"/> so the state record is pure,
/// serialization-trivial data with no owned memory — suitable for persistence in any host store.
/// </remarks>
public sealed record DotState(ImmutableArray<byte> Replica, int Counter);
