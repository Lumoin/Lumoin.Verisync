namespace Lumoin.Verisync.Core;

/// <summary>
/// One vertex of a serialized <see cref="Rga{TValue}"/>: the element's identity, the identity of the
/// element it was inserted after (or <see langword="null"/> for a head insert), and its value.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Id">The element's serialized identity.</param>
/// <param name="Predecessor">The serialized identity of the element this one was inserted after, or <see langword="null"/> for a head insert.</param>
/// <param name="Value">The element value.</param>
public sealed record RgaVertexEntry<TValue>(DotState Id, DotState? Predecessor, TValue Value);
