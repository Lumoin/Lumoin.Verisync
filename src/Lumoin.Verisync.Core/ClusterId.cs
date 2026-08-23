using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Identifies the chain a QuePaxa versioned register runs: the fixed-width digest of a genesis
/// configuration's member array, carried unchanged by every later configuration.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ClusterId"/> is a value type whose <see cref="Size"/> bytes live inline in whatever record,
/// field, or frame holds it, on the same inline fixed-identity idiom as <see cref="ReplicaId"/>. It is public
/// protocol state: every record carries it and every host compares it against its own.
/// </para>
/// <para>
/// Genesis cannot be made agreed, because agreement is what genesis bootstraps. What the identity buys is that
/// disagreement fails closed instead of forking: two hosts booted from divergent genesis member lists mint
/// different identities, decline each other's records, and lose progress rather than agreement. Two
/// independently bootstrapped clusters wired together by operator error are refused by the same comparison.
/// </para>
/// <para>
/// The digest is <em>order-sensitive</em> over the member array. The order is load-bearing, because the first
/// member is the bootstrap leader, so two hosts holding the same members in different orders must mint
/// different identities and block, rather than agree on an identity while splitting the bootstrap leader.
/// </para>
/// <para>
/// The hash algorithm is fixed rather than injected. The identity has to be identical on every host that reads
/// the same genesis member list, so a caller-supplied digest function would be one more fleet-wide setting a
/// deployment can split on, which is the exact fork this type exists to close. The fixed algorithm is also
/// what makes the width exactly <see cref="Size"/> bytes.
/// </para>
/// <para>
/// This is not authentication. A host that lies about its identity is not detected here; the comparison is
/// exact under crash faults and worthless against a liar.
/// </para>
/// </remarks>
[InlineArray(Size)]
public struct ClusterId: IEquatable<ClusterId>, IComparable<ClusterId>
{
    /// <summary>The fixed width of a cluster identifier in bytes.</summary>
    public const int Size = 32;

    //The digest is domain-separated so that a cluster identity can never coincide with a digest some other
    //part of the stack computes over the same member bytes.
    private static ReadOnlySpan<byte> GenesisDomain => "Lumoin.Verisync.QuePaxa.ClusterId.v1"u8;

    private byte element0;


    /// <summary>
    /// Constructs a <see cref="ClusterId"/> by copying exactly <see cref="Size"/> bytes from
    /// <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The identifier bytes; must be exactly <see cref="Size"/> bytes long.</param>
    /// <returns>A new <see cref="ClusterId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly <see cref="Size"/> bytes long.</exception>
    public static ClusterId FromSpan(ReadOnlySpan<byte> source)
    {
        if(source.Length != Size)
        {
            throw new ArgumentException($"ClusterId requires exactly {Size} bytes; got {source.Length}.", nameof(source));
        }

        ClusterId result = default;
        source.CopyTo(result);

        return result;
    }


    /// <summary>
    /// Mints the identity of the chain whose genesis configuration lists <paramref name="genesisMembers"/> in
    /// that order.
    /// </summary>
    /// <param name="genesisMembers">The genesis configuration's member array, in its agreed order.</param>
    /// <returns>The chain identity every host reading that same list in that same order computes.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="genesisMembers"/> is default or empty.</exception>
    /// <remarks>
    /// The digest covers a domain separator, the member count, and every member's bytes in array order, so a
    /// reordered list yields a different identity and a list of a different length can never encode as another
    /// list's bytes. Only genesis mints an identity; <see cref="QuePaxaConfiguration.With(HostId)"/> and
    /// <see cref="QuePaxaConfiguration.Without(ReplicaId)"/> carry the minted one forward unchanged, because a
    /// membership change stays on the chain it changes.
    /// </remarks>
    public static ClusterId FromGenesisMembers(ImmutableArray<HostId> genesisMembers)
    {
        if(genesisMembers.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A cluster identity requires at least one genesis member.", nameof(genesisMembers));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(GenesisDomain);

        Span<byte> memberCount = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(memberCount, genesisMembers.Length);
        hash.AppendData(memberCount);

        foreach(HostId member in genesisMembers)
        {
            hash.AppendData(member.Replica.AsSpan());
            hash.AppendData(member.Incarnation.AsSpan());
        }

        Span<byte> digest = stackalloc byte[Size];
        hash.GetCurrentHash(digest);

        return FromSpan(digest);
    }


    /// <summary>Returns a read-only span over the <see cref="Size"/> bytes of this identifier.</summary>
    /// <returns>The identifier bytes.</returns>
    public readonly ReadOnlySpan<byte> AsSpan()
    {
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in element0), Size);
    }


    /// <summary>Copies the bytes of this identifier into <paramref name="destination"/>.</summary>
    /// <param name="destination">The destination span; must be at least <see cref="Size"/> bytes.</param>
    public readonly void CopyTo(Span<byte> destination)
    {
        AsSpan().CopyTo(destination);
    }


    /// <summary>
    /// Allocates a fresh array containing the bytes of this identifier. Prefer <see cref="AsSpan"/> for reads;
    /// use this only where an array shape is unavoidable.
    /// </summary>
    /// <returns>A new array holding the identifier bytes.</returns>
    public readonly byte[] ToArray()
    {
        return AsSpan().ToArray();
    }


    /// <summary>Compares this identifier with <paramref name="other"/> by lexicographic byte order.</summary>
    /// <param name="other">The identifier to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public readonly int CompareTo(ClusterId other)
    {
        return AsSpan().SequenceCompareTo(other.AsSpan());
    }


    /// <inheritdoc/>
    public readonly bool Equals(ClusterId other)
    {
        return AsSpan().SequenceEqual(other.AsSpan());
    }


    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is ClusterId other && Equals(other);


    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        //The bytes are a digest and therefore well-distributed, so the leading word is a sound hash.
        return BinaryPrimitives.ReadInt32LittleEndian(AsSpan());
    }


    /// <summary>Determines whether two identifiers contain identical bytes.</summary>
    public static bool operator ==(ClusterId left, ClusterId right) => left.Equals(right);

    /// <summary>Determines whether two identifiers differ in their bytes.</summary>
    public static bool operator !=(ClusterId left, ClusterId right) => !left.Equals(right);

    /// <summary>Determines whether <paramref name="left"/> sorts before <paramref name="right"/>.</summary>
    public static bool operator <(ClusterId left, ClusterId right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> sorts before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(ClusterId left, ClusterId right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> sorts after <paramref name="right"/>.</summary>
    public static bool operator >(ClusterId left, ClusterId right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> sorts after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(ClusterId left, ClusterId right) => left.CompareTo(right) >= 0;


    /// <inheritdoc/>
    public override readonly string ToString()
    {
        ReadOnlySpan<byte> span = AsSpan();

        return $"ClusterId({Size} bytes, {Convert.ToHexStringLower(span[..Math.Min(span.Length, 8)])}...)";
    }
}
