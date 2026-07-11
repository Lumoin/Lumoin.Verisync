using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A coalesced two-range run of dotted tombstones for a serialized compactable <see cref="Rga{TValue}"/>: for
/// every <c>i</c> in <c>[0, TargetTo - TargetFrom]</c> the target dot <c>(TargetReplica, TargetFrom + i)</c>
/// carries EXACTLY the single remove-dot <c>(RemoveReplica, RemoveFrom + i)</c>.
/// </summary>
/// <param name="TargetReplica">The removed elements' replica raw identifier bytes.</param>
/// <param name="TargetFrom">The first removed counter in the span; at least one.</param>
/// <param name="TargetTo">The last removed counter in the span; at least <see cref="TargetFrom"/>.</param>
/// <param name="RemoveReplica">The removing replica's raw identifier bytes — the axis every remove-dot in the span sits on.</param>
/// <param name="RemoveFrom">The first remove-dot counter in the span; at least one. The remove counters advance in lockstep with the targets.</param>
/// <remarks>
/// The replicas are held as raw bytes rather than live <see cref="ReplicaId"/> values so the state record is
/// pure, serialization-trivial data with no owned memory. A single-replica contiguous deletion pass — one
/// replica removing a run of another replica's consecutive elements — packs into a single span even though it
/// carries two dot ranges: the run-length win survives the move to dotted removes.
/// </remarks>
public sealed record RgaTombstoneSpan(ImmutableArray<byte> TargetReplica, int TargetFrom, int TargetTo, ImmutableArray<byte> RemoveReplica, int RemoveFrom);
