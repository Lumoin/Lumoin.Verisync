using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Identifies one instance of a durable store: fixed-width random bytes minted when the store is first
/// created and carried unchanged for as long as that store's contents survive.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ReplicaId"/> names a member of a configuration. It does not name the store answering for that
/// member, and the two are not the same thing: a store wiped and restarted under the same replica identity, or
/// one identity provisioned onto two hosts, yields two stores holding divergent state that both answer as that
/// member. Quorums are counted over distinct members, so both are counted once, and two quorums can form that
/// intersect only at a member whose two stores disagree.
/// </para>
/// <para>
/// The incarnation is what makes those two stores distinguishable. It is minted with the store rather than
/// assigned by an operator, so a store that lost its contents cannot present the incarnation the configuration
/// admitted; there is nothing left to present it from. A member is therefore admitted as an identity bound to
/// an incarnation, and a host answering for an admitted identity under a different incarnation is refused.
/// </para>
/// <para>
/// This is not authentication. The value is a claim an answering host makes about itself, unsigned and
/// unverifiable, so it is exact under the crash faults this protocol assumes and worthless against a host that
/// lies. A deployment needing more owes its transport authentication.
/// </para>
/// <para>
/// The width is smaller than <see cref="ReplicaId.Size"/> because an incarnation carries no order, no address,
/// and no meaning outside equality with the one the configuration admitted. It ports to a Rust
/// <c>[u8; 16]</c> directly.
/// </para>
/// </remarks>
[InlineArray(Size)]
public struct StoreIncarnation: IEquatable<StoreIncarnation>
{
    /// <summary>The fixed width of a store incarnation in bytes.</summary>
    public const int Size = 16;

    private byte element0;


    /// <summary>
    /// Constructs a <see cref="StoreIncarnation"/> by copying exactly <see cref="Size"/> bytes from
    /// <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The incarnation bytes; must be exactly <see cref="Size"/> bytes long.</param>
    /// <returns>A new <see cref="StoreIncarnation"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not exactly <see cref="Size"/> bytes long.</exception>
    public static StoreIncarnation FromSpan(ReadOnlySpan<byte> source)
    {
        if(source.Length != Size)
        {
            throw new ArgumentException($"StoreIncarnation requires exactly {Size} bytes; got {source.Length}.", nameof(source));
        }

        StoreIncarnation result = default;
        source.CopyTo(result);

        return result;
    }


    /// <summary>
    /// Generates a new <see cref="StoreIncarnation"/> using the supplied entropy source.
    /// </summary>
    /// <param name="fillEntropy">
    /// The entropy source. Must fill the entire span with cryptographically random bytes.
    /// </param>
    /// <returns>A new <see cref="StoreIncarnation"/> containing cryptographically random bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="fillEntropy"/> is <see langword="null"/>.</exception>
    public static StoreIncarnation Generate(FillEntropyDelegate fillEntropy)
    {
        ArgumentNullException.ThrowIfNull(fillEntropy);

        StoreIncarnation result = default;
        fillEntropy(MemoryMarshal.CreateSpan(ref result.element0, Size));

        return result;
    }


    /// <summary>
    /// Generates a new <see cref="StoreIncarnation"/> using the platform CSPRNG
    /// (<see cref="RandomNumberGenerator.Fill(Span{byte})"/>).
    /// </summary>
    /// <returns>A new <see cref="StoreIncarnation"/> containing cryptographically random bytes.</returns>
    public static StoreIncarnation Generate()
    {
        return Generate(RandomNumberGenerator.Fill);
    }


    /// <summary>Returns a read-only span over the <see cref="Size"/> bytes of this incarnation.</summary>
    /// <returns>The incarnation bytes.</returns>
    public readonly ReadOnlySpan<byte> AsSpan()
    {
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in element0), Size);
    }


    /// <summary>Copies the bytes of this incarnation into <paramref name="destination"/>.</summary>
    /// <param name="destination">The destination span; must be at least <see cref="Size"/> bytes.</param>
    public readonly void CopyTo(Span<byte> destination)
    {
        AsSpan().CopyTo(destination);
    }


    /// <summary>
    /// Allocates a fresh array containing the bytes of this incarnation. Prefer <see cref="AsSpan"/> for reads;
    /// use this only where an array shape is unavoidable.
    /// </summary>
    /// <returns>A new array holding the incarnation bytes.</returns>
    public readonly byte[] ToArray()
    {
        return AsSpan().ToArray();
    }


    /// <inheritdoc/>
    public readonly bool Equals(StoreIncarnation other)
    {
        return AsSpan().SequenceEqual(other.AsSpan());
    }


    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is StoreIncarnation other && Equals(other);


    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        //The bytes are well-distributed (random incarnations), so the leading word is a sound hash.
        return BinaryPrimitives.ReadInt32LittleEndian(AsSpan());
    }


    /// <summary>Determines whether two incarnations contain identical bytes.</summary>
    public static bool operator ==(StoreIncarnation left, StoreIncarnation right) => left.Equals(right);

    /// <summary>Determines whether two incarnations differ in their bytes.</summary>
    public static bool operator !=(StoreIncarnation left, StoreIncarnation right) => !left.Equals(right);


    /// <inheritdoc/>
    public override readonly string ToString()
    {
        ReadOnlySpan<byte> span = AsSpan();

        return $"StoreIncarnation({Size} bytes, {Convert.ToHexStringLower(span[..Math.Min(span.Length, 8)])}...)";
    }
}
