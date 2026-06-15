namespace Lumoin.Verisync.Core;

/// <summary>
/// A single position on an item's index walk: the symbol <see cref="Index"/> the walk currently visits and
/// the internal <see cref="State"/> that <see cref="ReconciliationIndexWalk.Next(ReconciliationWalkPosition)"/>
/// advances to derive the next gap. The walk is a pure function of item bytes, so this position carries no
/// dependence on contract, checksum key, or call history.
/// </summary>
/// <param name="Index">The symbol index this position visits. Starts at zero and strictly increases.</param>
/// <param name="State">The opaque generator state threading the gap sequence forward.</param>
public readonly record struct ReconciliationWalkPosition(long Index, ulong State);
