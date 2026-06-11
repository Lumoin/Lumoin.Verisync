using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The addressing type of the checkpoint-offset sequence strategy: an element is anchored either at a
/// position of the agreed base snapshot (<see cref="AtBase(int)"/>, with <see cref="Head"/> as the
/// virtual position before the first), or at a live element by its <see cref="Dot"/> identity
/// (<see cref="AtLive(Dot)"/>).
/// </summary>
/// <remarks>
/// Offsets into the base are stable because the base is immutable by consensus — they are positions in
/// an agreed snapshot, not positions in a live view. Live identities are ordinary dots. The two cases
/// never mix: an anchor is exactly one of them.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class OffsetAnchor: IEquatable<OffsetAnchor>
{
    private OffsetAnchor(int baseOffset, Dot? liveId)
    {
        BaseOffset = baseOffset;
        LiveId = liveId;
    }


    /// <summary>The virtual position before the first base element.</summary>
    public static OffsetAnchor Head { get; } = new(-1, null);


    /// <summary>The base offset: <c>-1</c> for <see cref="Head"/>, the position otherwise. Meaningless when <see cref="IsLive"/>.</summary>
    public int BaseOffset { get; }

    /// <summary>The live element's identity, or <see langword="null"/> for a base anchor.</summary>
    public Dot? LiveId { get; }

    /// <summary>Whether this anchors at a live element rather than a base position.</summary>
    public bool IsLive => LiveId is not null;


    /// <summary>
    /// Anchors at the base element at <paramref name="offset"/>.
    /// </summary>
    /// <param name="offset">The zero-based offset into the agreed base snapshot.</param>
    /// <returns>The anchor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="offset"/> is negative.</exception>
    public static OffsetAnchor AtBase(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        return new OffsetAnchor(offset, null);
    }


    /// <summary>
    /// Anchors at the live element identified by <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The live element's identity.</param>
    /// <returns>The anchor.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="id"/> is <see langword="null"/>.</exception>
    public static OffsetAnchor AtLive(Dot id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return new OffsetAnchor(-1, id);
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] OffsetAnchor? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return BaseOffset == other.BaseOffset && Equals(LiveId, other.LiveId);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OffsetAnchor other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(BaseOffset, LiveId);


    private string DebuggerDisplay => IsLive ? $"OffsetAnchor: live {LiveId}" : BaseOffset < 0 ? "OffsetAnchor: head" : $"OffsetAnchor: base[{BaseOffset}]";
}
