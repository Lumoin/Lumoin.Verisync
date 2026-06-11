namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable form of an <see cref="OffsetAnchor"/>: a base offset paired with an optional live
/// identity, in the same one-canonical-shape-per-anchor discipline as the live type.
/// </summary>
/// <param name="BaseOffset">The base offset: <c>-1</c> for the head and for a live anchor, the zero-based base position otherwise.</param>
/// <param name="LiveId">The live element's serialized identity, or <see langword="null"/> for the head and for a base anchor.</param>
/// <remarks>
/// Exactly one shape per anchor, mirroring <see cref="OffsetAnchor"/>: the head is
/// <c>BaseOffset == -1</c> with a null <see cref="LiveId"/>; a base anchor is <c>BaseOffset &gt;= 0</c>
/// with a null <see cref="LiveId"/>; a live anchor carries a non-null <see cref="LiveId"/> and MUST have
/// <c>BaseOffset == -1</c>, so a live and a base anchor never share a representation.
/// </remarks>
public sealed record OffsetAnchorState(int BaseOffset, DotState? LiveId);
