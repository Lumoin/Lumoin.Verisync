using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The index walk that decides which coded symbols an item contributes to. The walk is a pure function of
/// item bytes: it starts at index zero (so every item touches symbol zero), then strictly increases with
/// geometrically growing gaps, so the probability an item maps to index <c>i</c> falls as
/// <c>1 / (1 + 0.5 * i)</c>. Because every item touches index zero, the decoder's cell zero is the last to
/// clear and so witnesses completion.
/// </summary>
/// <remarks>
/// The walk seed is SipHash-2-4 of the item under a fixed walk key, and each subsequent gap is drawn from a
/// splitmix64 step fed through an inverse cumulative distribution. The walk depends on item bytes only — not
/// on the reconciliation contract, the checksum key, or call history — so two replicas derive identical
/// streams for the same item, which is exactly what makes the symbol-wise XOR of two streams the stream of
/// their symmetric difference.
/// </remarks>
public static class ReconciliationIndexWalk
{
    //The little-endian halves of the sixteen ASCII bytes that seed the walk; distinct from any checksum key
    //so the walk never correlates with the cell checksums.
    private const ulong WalkKeyLow = 0x636E797369726576UL;
    private const ulong WalkKeyHigh = 0x31302D6B6C61772DUL;


    /// <summary>
    /// Begins the walk for <paramref name="item"/>, returning the position at index zero seeded from the
    /// item's bytes.
    /// </summary>
    /// <param name="item">The item bytes to walk. Must be non-empty.</param>
    /// <returns>The starting position, whose index is zero.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/> is empty.</exception>
    public static ReconciliationWalkPosition Start(ReadOnlySpan<byte> item)
    {
        if(item.IsEmpty)
        {
            throw new ArgumentException("An index walk cannot start from an empty item.", nameof(item));
        }

        return new ReconciliationWalkPosition(0, ReconciliationChecksum.Compute(WalkKeyLow, WalkKeyHigh, item));
    }


    /// <summary>
    /// Advances <paramref name="position"/> to the next index the walk visits. The gap is at least one, so
    /// the walk strictly increases and visits index zero exactly once.
    /// </summary>
    /// <param name="position">The current walk position.</param>
    /// <returns>The next position, at a strictly greater index.</returns>
    /// <exception cref="OverflowException">Thrown when the next index would exceed <see cref="long.MaxValue"/>.</exception>
    public static ReconciliationWalkPosition Next(ReconciliationWalkPosition position)
    {
        ulong state = unchecked(position.State + 0x9E3779B97F4A7C15UL);
        ulong t = state;
        t ^= t >> 30;
        t = unchecked(t * 0xBF58476D1CE4E5B9UL);
        t ^= t >> 27;
        t = unchecked(t * 0x94D049BB133111EBUL);
        t ^= t >> 31;
        double r = (t >> 11) * (1.0 / 9007199254740992.0);
        long gap = Math.Max(1L, checked((long)Math.Ceiling((1.5 + position.Index) * ((1.0 / Math.Sqrt(1.0 - r)) - 1.0))));

        return new ReconciliationWalkPosition(checked(position.Index + gap), state);
    }
}
