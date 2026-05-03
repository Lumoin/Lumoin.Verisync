using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="Rga{TValue}"/>: its causal context, vertices, and tombstones.
/// Obtain it with <see cref="Rga{TValue}.ToState"/> and reconstruct with <see cref="Rga{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Vertices">The serialized vertices, visible and tombstoned alike.</param>
/// <param name="Tombstones">The serialized identities of the tombstoned elements.</param>
public sealed record RgaState<TValue>(VectorClockState Context, ImmutableArray<RgaVertexEntry<TValue>> Vertices, ImmutableArray<DotState> Tombstones);
