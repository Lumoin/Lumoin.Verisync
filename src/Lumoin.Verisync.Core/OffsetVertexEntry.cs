namespace Lumoin.Verisync.Core;

/// <summary>
/// One vertex of a serialized <see cref="OffsetAnchoredSequence{TValue}"/>: the element's identity, the
/// anchor it was inserted at (a base position, another live element, or the head), and its value.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Id">The element's serialized identity.</param>
/// <param name="Anchor">The serialized anchor the element was inserted at.</param>
/// <param name="Value">The element value.</param>
public sealed record OffsetVertexEntry<TValue>(DotState Id, OffsetAnchorState Anchor, TValue Value);
