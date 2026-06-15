namespace Lumoin.Verisync.Core;

/// <summary>
/// A thin carrier of one replica's causal context — the <see cref="VectorClock"/> of events it has observed —
/// exchanged once per remove-aware session so each side can classify a decoded dot as a fresh add or as an
/// observed-and-removed tombstone. The context is shipped whole (a vector clock is one small entry per
/// replica), never reconciled through the coded stream; only the present dotted-entry set is reconciled.
/// </summary>
/// <param name="Clock">The serialized causal context, every event this replica has observed.</param>
/// <remarks>
/// This is a state carrier, matching the positional-record shape of <see cref="DottedVersionVectorSetState{TValue}"/>
/// rather than the validating-message shape of <see cref="ReconciliationFetch"/>: the constructor neither
/// validates nor copies, and equality is not customized. The clock is validated and compared by reconstructing
/// it with <see cref="VectorClock.FromState"/>, exactly as the other state records are — the synthesized record
/// equality would compare the <see cref="System.Collections.Immutable.ImmutableArray{T}"/> of clock entries by
/// reference identity, never by value, so a caller that needs causal equality reconstructs both clocks and
/// compares those. An empty clock is legal: it is a fresh replica's context, having observed no events.
/// </remarks>
public sealed record ReconciliationContext(VectorClockState Clock);
