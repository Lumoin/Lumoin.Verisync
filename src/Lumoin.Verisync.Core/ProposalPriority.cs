using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The priority half of a proposal key: the random value that lets QuePaxa order competing proposals without
/// a leader, together with the two reserved endpoints the protocol gives a fixed meaning.
/// </summary>
/// <param name="Value">The priority value. Every 64-bit value is representable.</param>
/// <remarks>
/// <para>
/// Every <see cref="ulong"/> is a representable priority, so nothing is validated at construction; what is
/// constrained is what may be <em>drawn</em>, and that is <see cref="ProposalPrioritySourceDelegate"/>'s
/// contract. <see cref="None"/> is the aggregate's identity and is never drawn and never sent,
/// <see cref="Lowest"/> is where a declined reserved claim lands, and <see cref="Reserved"/> is the
/// round-one leader's top priority.
/// </para>
/// <para>
/// Priority unpredictability is load-bearing for liveness. QuePaxa's liveness argument assumes an adversary
/// that cannot see proposal contents, so an adversary that learns the priorities can schedule messages to
/// keep a round from converging. Safety does not depend on it, and nothing in this type can supply it. A
/// deployment supplies it by running the replica-to-replica links under TLS and by drawing from
/// <see cref="Cryptographic"/> or an equally unpredictable source.
/// </para>
/// <para>
/// <see cref="Reserved"/> is above two to the fifty-third, so any wire format that parses numbers as
/// doubles destroys it, and a mangled reserved priority silently becomes an ordinary one. A codec layer owes
/// round-trip tests pinned at <see cref="Reserved"/> and at one below it.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct ProposalPriority(ulong Value): IComparable<ProposalPriority>
{
    /// <summary>
    /// The absent priority, which is zero. It is the identity of the aggregate fold and is neither drawn nor
    /// sent; a proposal template carries it only as the placeholder a phase-zero redraw replaces.
    /// </summary>
    public static ProposalPriority None { get; } = new(0UL);

    /// <summary>
    /// The lowest ordinary priority, which is one. A recorder that declines a reserved claim records the
    /// proposal here rather than dropping it.
    /// </summary>
    public static ProposalPriority Lowest { get; } = new(1UL);

    /// <summary>
    /// The reserved top priority, which is the largest <see cref="ulong"/>. It is the round-one leader's
    /// claim, and it means something only at the protocol's first step.
    /// </summary>
    public static ProposalPriority Reserved { get; } = new(ulong.MaxValue);


    /// <summary>Whether this is the reserved top priority.</summary>
    public bool IsReserved => Value == Reserved.Value;

    /// <summary>Whether this is an ordinary priority, that is a value from <see cref="Lowest"/> up to one below <see cref="Reserved"/>.</summary>
    public bool IsOrdinary => Value >= Lowest.Value && Value < Reserved.Value;

    /// <summary>Whether this is the absent priority.</summary>
    public bool IsNone => Value == None.Value;


    /// <summary>
    /// Draws an ordinary priority from <paramref name="fillEntropy"/>: eight bytes read as a little-endian
    /// <see cref="ulong"/>, rejecting <see cref="None"/> and <see cref="Reserved"/> and redrawing.
    /// </summary>
    /// <param name="fillEntropy">The entropy source. It must fill the entire span with independent random bytes.</param>
    /// <returns>A priority satisfying <see cref="IsOrdinary"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="fillEntropy"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The legal range is the whole 64-bit space minus its two reserved endpoints, so the rejection sampling
    /// carries no modulo bias and terminates with probability one minus two to the minus sixty-three per
    /// draw. The entropy source is trusted to fill the span with independent random bytes, as
    /// <see cref="ReplicaId.Generate(FillEntropyDelegate)"/> trusts it. A source that writes a constant makes
    /// this loop spin, and neither a constant source nor a merely predictable one can be detected here, so
    /// the obligation is the caller's.
    /// </remarks>
    public static ProposalPriority DrawOrdinary(FillEntropyDelegate fillEntropy)
    {
        ArgumentNullException.ThrowIfNull(fillEntropy);

        Span<byte> drawn = stackalloc byte[sizeof(ulong)];
        while(true)
        {
            fillEntropy(drawn);
            ulong candidate = BinaryPrimitives.ReadUInt64LittleEndian(drawn);
            if(candidate != None.Value && candidate != Reserved.Value)
            {
                return new ProposalPriority(candidate);
            }
        }
    }


    /// <summary>
    /// The production priority source: <see cref="DrawOrdinary(FillEntropyDelegate)"/> over the platform
    /// CSPRNG.
    /// </summary>
    /// <remarks>
    /// The delegate instance is created once at type initialization. A test drives a seeded source instead.
    /// </remarks>
    public static ProposalPrioritySourceDelegate Cryptographic { get; } = static () => DrawOrdinary(RandomNumberGenerator.Fill);


    /// <summary>Compares this priority with <paramref name="other"/> by value.</summary>
    /// <param name="other">The priority to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(ProposalPriority other) => Value.CompareTo(other.Value);


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(ProposalPriority left, ProposalPriority right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(ProposalPriority left, ProposalPriority right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(ProposalPriority left, ProposalPriority right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(ProposalPriority left, ProposalPriority right) => left.CompareTo(right) >= 0;


    private string DebuggerDisplay => IsNone ? "ProposalPriority: none" : IsReserved ? "ProposalPriority: reserved" : $"ProposalPriority: {Value}";
}
