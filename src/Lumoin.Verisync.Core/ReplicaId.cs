using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Identifies a participating replica: fixed-width pseudonymous random bytes carrying no externally
/// meaningful structure — no IP addresses, no hostnames, no public-key fingerprints.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ReplicaId"/> is a value type whose <see cref="Size"/> bytes live inline in whatever frame,
/// field, array, or dictionary entry holds it. There is no separate allocation, no owner to dispose, and no
/// GC object per identity: an id's lifetime is exactly its container's. This mirrors the family's inline
/// fixed-identity idiom (Veritas <c>Digest32</c>) and ports to a Rust <c>[u8; 32]</c> directly.
/// </para>
/// <para>
/// A replica identity is <em>public protocol state</em> — acceptors, learners, and reads all see it, quorum
/// membership names replicas by it, and ballot tie-breaking depends on a total order over it
/// (<see cref="CompareTo(ReplicaId)"/>, lexicographic byte order). It is never a private witness; data that
/// must be cleared after use flows through <see cref="TaggedMemory"/>-based payloads instead.
/// </para>
/// <para>
/// A <see cref="ReplicaId"/> is distinct from whatever authorises an operation in the application's
/// authorisation model; the protocol-layer replica identity is a separate axis.
/// </para>
/// </remarks>
[InlineArray(Size)]
public struct ReplicaId: IEquatable<ReplicaId>, IComparable<ReplicaId>
{
    /// <summary>The fixed width of a replica identifier in bytes.</summary>
    public const int Size = 32;

    private byte element0;


    /// <summary>
    /// Constructs a <see cref="ReplicaId"/> by copying exactly <see cref="Size"/> bytes from
    /// <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The identifier bytes; must be exactly <see cref="Size"/> bytes long.</param>
    /// <returns>A new <see cref="ReplicaId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly <see cref="Size"/> bytes long.</exception>
    public static ReplicaId FromSpan(ReadOnlySpan<byte> source)
    {
        if(source.Length != Size)
        {
            throw new ArgumentException($"ReplicaId requires exactly {Size} bytes; got {source.Length}.", nameof(source));
        }

        ReplicaId result = default;
        source.CopyTo(result);

        return result;
    }


    /// <summary>
    /// Generates a new <see cref="ReplicaId"/> using the supplied entropy source.
    /// </summary>
    /// <param name="fillEntropy">
    /// The entropy source. Must fill the entire span with cryptographically random bytes.
    /// </param>
    /// <returns>A new <see cref="ReplicaId"/> containing cryptographically random bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="fillEntropy"/> is <see langword="null"/>.</exception>
    public static ReplicaId Generate(FillEntropyDelegate fillEntropy)
    {
        ArgumentNullException.ThrowIfNull(fillEntropy);

        ReplicaId result = default;
        fillEntropy(MemoryMarshal.CreateSpan(ref result.element0, Size));

        return result;
    }


    /// <summary>
    /// Generates a new <see cref="ReplicaId"/> using the platform CSPRNG
    /// (<see cref="RandomNumberGenerator.Fill(Span{byte})"/>).
    /// </summary>
    /// <returns>A new <see cref="ReplicaId"/> containing cryptographically random bytes.</returns>
    public static ReplicaId Generate()
    {
        return Generate(RandomNumberGenerator.Fill);
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


    /// <summary>
    /// Compares this identifier with <paramref name="other"/> by lexicographic byte order — the total order
    /// used for ballot tie-breaking.
    /// </summary>
    /// <param name="other">The identifier to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public readonly int CompareTo(ReplicaId other)
    {
        return AsSpan().SequenceCompareTo(other.AsSpan());
    }


    /// <inheritdoc/>
    public readonly bool Equals(ReplicaId other)
    {
        return AsSpan().SequenceEqual(other.AsSpan());
    }


    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is ReplicaId other && Equals(other);


    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        //The bytes are well-distributed (random ids), so the leading word is a sound hash.
        return BinaryPrimitives.ReadInt32LittleEndian(AsSpan());
    }


    /// <summary>Determines whether two identifiers contain identical bytes.</summary>
    public static bool operator ==(ReplicaId left, ReplicaId right) => left.Equals(right);

    /// <summary>Determines whether two identifiers differ in their bytes.</summary>
    public static bool operator !=(ReplicaId left, ReplicaId right) => !left.Equals(right);

    /// <summary>Determines whether <paramref name="left"/> sorts before <paramref name="right"/>.</summary>
    public static bool operator <(ReplicaId left, ReplicaId right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> sorts before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(ReplicaId left, ReplicaId right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> sorts after <paramref name="right"/>.</summary>
    public static bool operator >(ReplicaId left, ReplicaId right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> sorts after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(ReplicaId left, ReplicaId right) => left.CompareTo(right) >= 0;


    /// <inheritdoc/>
    public override readonly string ToString()
    {
        ReadOnlySpan<byte> span = AsSpan();

        return $"ReplicaId({Size} bytes, {Convert.ToHexStringLower(span[..Math.Min(span.Length, 8)])}...)";
    }
}
