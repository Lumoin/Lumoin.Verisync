using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The identities and the written values every arm of the harness uses.
/// </summary>
/// <remarks>
/// Identities come from fixed bytes rather than from generated entropy, so no measurement can depend on which
/// way a generated identity happened to sort. The byte is the index plus one, which keeps the all-zero
/// identity out of the harness and leaves a lane beyond the replica count available for the configurations
/// whose configured leader never writes.
/// </remarks>
internal static class HarnessIdentity
{
    /// <summary>The largest replica count the fixed-byte identity scheme addresses.</summary>
    public const int MaximumIndex = 254;


    /// <summary>The replica identity at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based index. Must be below <see cref="MaximumIndex"/>.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="index"/> is negative or not below <see cref="MaximumIndex"/>.</exception>
    public static ReplicaId Replica(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaximumIndex);

        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = (byte)(index + 1);

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>The proposer lane at <paramref name="index"/>, which is lane zero of that index's replica.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The lane.</returns>
    public static ProposerLane Lane(int index) => ProposerLane.For(Replica(index));


    /// <summary>The value writer <paramref name="writer"/> proposes.</summary>
    /// <param name="writer">The writer index.</param>
    /// <returns>The value.</returns>
    public static string Value(int writer) => $"w{writer}";
}
