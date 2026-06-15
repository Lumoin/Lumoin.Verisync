using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The opening message of a reconciliation session: the public part of a contract that a peer may see, plus
/// a <see cref="KeyCheck"/> tag that lets both sides confirm they hold the same checksum key without putting
/// the key on the wire. The item domain, item width, and checksum width pin the byte spaces and field widths
/// the coded streams subtract over; an offer that does not match the local contract must abort the session
/// before any symbol flows.
/// </summary>
/// <remarks>
/// The offer never carries key bytes. <see cref="KeyCheck"/> is a pseudo-random tag over a fixed public input
/// under the contract's key, so equal tags imply the same 128-bit key with overwhelming probability and
/// unequal tags abort the session up front instead of letting symbol-wise subtraction silently fail to peel.
/// The constructor copies the key-check argument into a private array, so a caller may reuse its buffer.
/// </remarks>
public sealed record ReconciliationOffer
{
    //The fixed public input the key check is a pseudo-random tag over; the key itself never leaves the host.
    private const string KeyCheckInput = "verisync-keyck-1";


    private byte[] KeyCheckBytes { get; }


    /// <summary>
    /// Initializes an offer, validating that every field is within the range two honest streams can share and
    /// that the key check is exactly eight bytes, copying it so the caller may reuse the source buffer.
    /// </summary>
    /// <param name="itemDomain">The byte space items are drawn from.</param>
    /// <param name="itemWidth">The exact item width in bytes, in the inclusive range one through 1024.</param>
    /// <param name="checksumWidth">The cell checksum width in bytes, in the inclusive range one through eight.</param>
    /// <param name="keyCheck">The eight-byte key-check tag, the little-endian digest of the fixed public input under the contract's key.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="itemDomain"/> is not a defined value, when <paramref name="itemWidth"/> is
    /// outside one through 1024, or when <paramref name="checksumWidth"/> is outside one through eight.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="keyCheck"/>'s length is not eight.</exception>
    public ReconciliationOffer(ReconciliationItemDomain itemDomain, int itemWidth, int checksumWidth, ReadOnlyMemory<byte> keyCheck)
    {
        if(itemDomain is not (ReconciliationItemDomain.ContentHash or ReconciliationItemDomain.Structural))
        {
            throw new ArgumentOutOfRangeException(nameof(itemDomain), itemDomain, "The item domain must be a defined value.");
        }

        if(itemWidth is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(itemWidth), itemWidth, "The item width must be between one and 1024 bytes.");
        }

        if(checksumWidth is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(checksumWidth), checksumWidth, "The checksum width must be between one and eight bytes.");
        }

        if(keyCheck.Length != 8)
        {
            throw new ArgumentException("A key check must be exactly eight bytes.", nameof(keyCheck));
        }

        ItemDomain = itemDomain;
        ItemWidth = itemWidth;
        ChecksumWidth = checksumWidth;
        KeyCheckBytes = keyCheck.ToArray();
    }


    /// <summary>The byte space items are drawn from.</summary>
    public ReconciliationItemDomain ItemDomain { get; }

    /// <summary>The exact item width in bytes the coded streams subtract over.</summary>
    public int ItemWidth { get; }

    /// <summary>The cell checksum width in bytes the coded streams subtract over.</summary>
    public int ChecksumWidth { get; }

    /// <summary>The eight little-endian key-check bytes, a pseudo-random tag over a fixed public input that never reveals the key.</summary>
    public ReadOnlyMemory<byte> KeyCheck => KeyCheckBytes;


    /// <summary>
    /// Builds the offer a peer may see for <paramref name="contract"/>, deriving the key check as the eight
    /// little-endian bytes of the keyed digest of the fixed public input under the contract's key.
    /// </summary>
    /// <param name="contract">The local contract whose public part the offer pins.</param>
    /// <returns>An offer whose domain, widths, and key check come from <paramref name="contract"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> is <see langword="null"/>.</exception>
    public static ReconciliationOffer FromContract(ReconciliationContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        ReadOnlySpan<byte> input = Encoding.ASCII.GetBytes(KeyCheckInput);
        ulong tag = ReconciliationChecksum.Compute(contract.ChecksumKeyLow, contract.ChecksumKeyHigh, input);

        var keyCheck = new byte[8];
        ReconciliationChecksum.Write(tag, keyCheck);

        return new ReconciliationOffer(contract.ItemDomain, contract.ItemWidth, contract.ChecksumWidth, keyCheck);
    }


    /// <summary>
    /// Determines whether this offer pins the same public contract as <paramref name="contract"/>: the domain,
    /// the item width, and the checksum width match, and the key check equals the one this contract derives.
    /// A false result is a hard mismatch that must abort the session before any symbol flows.
    /// </summary>
    /// <param name="contract">The local contract to match against.</param>
    /// <returns><see langword="true"/> when the offer pins the same public contract as <paramref name="contract"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> is <see langword="null"/>.</exception>
    public bool Matches(ReconciliationContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if(ItemDomain != contract.ItemDomain || ItemWidth != contract.ItemWidth || ChecksumWidth != contract.ChecksumWidth)
        {
            return false;
        }

        return KeyCheckBytes.AsSpan().SequenceEqual(FromContract(contract).KeyCheck.Span);
    }


    /// <summary>Determines whether <paramref name="other"/> pins the same domain, widths, and key-check bytes.</summary>
    /// <param name="other">The offer to compare with.</param>
    /// <returns><see langword="true"/> when the domain, widths, and key-check bytes all match.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ReadOnlyMemory{T}"/>
    /// key check by reference identity; offer equality is byte-sequence equality over the key check.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationOffer? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return ItemDomain == other.ItemDomain
            && ItemWidth == other.ItemWidth
            && ChecksumWidth == other.ChecksumWidth
            && KeyCheckBytes.AsSpan().SequenceEqual(other.KeyCheckBytes);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ItemDomain);
        hash.Add(ItemWidth);
        hash.Add(ChecksumWidth);
        hash.AddBytes(KeyCheckBytes);

        return hash.ToHashCode();
    }
}
