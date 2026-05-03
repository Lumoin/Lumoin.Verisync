using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="VectorClock"/>: its per-replica entries. Obtain it with
/// <see cref="VectorClock.ToState"/> and reconstruct with <see cref="VectorClock.FromState"/>.
/// </summary>
/// <param name="Entries">The per-replica counter entries.</param>
public sealed record VectorClockState(ImmutableArray<ReplicaCounterEntry> Entries);
