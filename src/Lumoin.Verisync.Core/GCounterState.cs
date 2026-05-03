using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="GCounter"/>: its per-replica entries. Obtain it with
/// <see cref="GCounter.ToState"/> and reconstruct with <see cref="GCounter.FromState"/>.
/// </summary>
/// <param name="Entries">The per-replica counter entries.</param>
public sealed record GCounterState(ImmutableArray<ReplicaCounterEntry> Entries);
