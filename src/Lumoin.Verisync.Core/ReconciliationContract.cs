using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Everything two reconciliation streams must agree on before their symbol-wise subtraction is meaningful:
/// the item domain, the item width, the checksum width, and the checksum key. Peers with unequal contracts
/// must refuse to reconcile before any symbol flows — the wire phase treats a contract like a strategy
/// identifier, an unequal one is a hard mismatch, not a negotiation.
/// </summary>
/// <remarks>
/// All members are value types, so the record's synthesized equality is exactly content equality over the
/// contract fields. Production checksum widths are four or eight bytes and the public constructor enforces that
/// floor (see <see cref="MinimumProductionChecksumWidth"/>); the narrower one-through-three-byte widths exist
/// only for adversarial tests of the masquerade bound, where a deliberately weak checksum lets a degree-two
/// cell masquerade as a decoded item, and come only from the internal <see cref="ForAdversarialTesting"/>
/// factory.
/// </remarks>
public sealed record ReconciliationContract
{
    /// <summary>The low 64 bits of the well-known checksum key, the little-endian first eight bytes of the trusted-replica default key.</summary>
    public const ulong WellKnownChecksumKeyLow = 0x636E797369726576UL;

    /// <summary>The high 64 bits of the well-known checksum key, the little-endian last eight bytes of the trusted-replica default key.</summary>
    public const ulong WellKnownChecksumKeyHigh = 0x31302D6D7573632DUL;

    /// <summary>
    /// The smallest checksum width the public constructor admits. Below four bytes the per-decode masquerade
    /// union bound — roughly <c>d·ln d · 2^(−8·width)</c> for a difference of <c>d</c> items — becomes material
    /// at realistic difference sizes, so narrower widths are constructible only through the internal
    /// adversarial-test factory <see cref="ForAdversarialTesting"/>.
    /// </summary>
    public const int MinimumProductionChecksumWidth = 4;


    /// <summary>
    /// Initializes a production contract, validating that every field is within the range two honest streams can
    /// share and that the checksum width meets the production floor.
    /// </summary>
    /// <param name="itemDomain">The byte space items are drawn from.</param>
    /// <param name="itemWidth">The exact item width in bytes, in the inclusive range one through 1024.</param>
    /// <param name="checksumWidth">The cell checksum width in bytes, in the inclusive range four through eight.</param>
    /// <param name="checksumKeyLow">The low 64 bits of the checksum key.</param>
    /// <param name="checksumKeyHigh">The high 64 bits of the checksum key.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="itemDomain"/> is not a defined value, when <paramref name="itemWidth"/> is
    /// outside one through 1024, or when <paramref name="checksumWidth"/> is outside
    /// <see cref="MinimumProductionChecksumWidth"/> through eight.
    /// </exception>
    public ReconciliationContract(ReconciliationItemDomain itemDomain, int itemWidth, int checksumWidth, ulong checksumKeyLow, ulong checksumKeyHigh)
        : this(itemDomain, itemWidth, checksumWidth, checksumKeyLow, checksumKeyHigh, enforceProductionFloor: true)
    {
    }


    /// <summary>
    /// Builds a contract with the checksum-width production floor lifted, admitting the one-through-three-byte
    /// widths for adversarial tests that deliberately provoke a masquerade; zero and widths above eight are
    /// still rejected. Not a production path — a deliberately weak checksum lets a degree-two cell masquerade as
    /// a decoded item.
    /// </summary>
    /// <param name="itemDomain">The byte space items are drawn from.</param>
    /// <param name="itemWidth">The exact item width in bytes, in the inclusive range one through 1024.</param>
    /// <param name="checksumWidth">The cell checksum width in bytes, in the inclusive range one through eight.</param>
    /// <param name="checksumKeyLow">The low 64 bits of the checksum key.</param>
    /// <param name="checksumKeyHigh">The high 64 bits of the checksum key.</param>
    /// <returns>A contract carrying the requested checksum width without the production floor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="itemDomain"/> is not a defined value, when <paramref name="itemWidth"/> is
    /// outside one through 1024, or when <paramref name="checksumWidth"/> is outside one through eight.
    /// </exception>
    internal static ReconciliationContract ForAdversarialTesting(ReconciliationItemDomain itemDomain, int itemWidth, int checksumWidth, ulong checksumKeyLow, ulong checksumKeyHigh)
    {
        return new ReconciliationContract(itemDomain, itemWidth, checksumWidth, checksumKeyLow, checksumKeyHigh, enforceProductionFloor: false);
    }


    private ReconciliationContract(ReconciliationItemDomain itemDomain, int itemWidth, int checksumWidth, ulong checksumKeyLow, ulong checksumKeyHigh, bool enforceProductionFloor)
    {
        if(itemDomain is not (ReconciliationItemDomain.ContentHash or ReconciliationItemDomain.Structural))
        {
            throw new ArgumentOutOfRangeException(nameof(itemDomain), itemDomain, "The item domain must be a defined value.");
        }

        if(itemWidth is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(itemWidth), itemWidth, "The item width must be between one and 1024 bytes.");
        }

        int minimumChecksumWidth = enforceProductionFloor ? MinimumProductionChecksumWidth : 1;
        if(checksumWidth < minimumChecksumWidth || checksumWidth > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(checksumWidth), checksumWidth, $"The checksum width must be between {minimumChecksumWidth} and 8 bytes.");
        }

        ItemDomain = itemDomain;
        ItemWidth = itemWidth;
        ChecksumWidth = checksumWidth;
        ChecksumKeyLow = checksumKeyLow;
        ChecksumKeyHigh = checksumKeyHigh;
    }


    /// <summary>The byte space items are drawn from.</summary>
    public ReconciliationItemDomain ItemDomain { get; }

    /// <summary>The exact item width in bytes. Items shorter or longer are a contract violation, never padded.</summary>
    public int ItemWidth { get; }

    /// <summary>
    /// The cell checksum width in bytes. Wider checksums tighten the masquerade bound: a mixed cell passes a
    /// single purity check with probability <c>2^(−8·width)</c>, and a whole decode compounds that over
    /// roughly <c>d·ln d</c> checks for a difference of <c>d</c> items (the per-decode union bound on
    /// <see cref="ReconciliationDecoder"/>). Size the width against the expected difference: eight bytes
    /// keeps the per-decode bound negligible at any realistic scale; four bytes suits small or advisory
    /// differences but grows material past tens of thousands of items; narrower widths exist only for
    /// adversarial tests that deliberately provoke a masquerade.
    /// </summary>
    public int ChecksumWidth { get; }

    /// <summary>The low 64 bits of the checksum key.</summary>
    public ulong ChecksumKeyLow { get; }

    /// <summary>The high 64 bits of the checksum key.</summary>
    public ulong ChecksumKeyHigh { get; }


    /// <summary>
    /// The trusted-replica default contract: content-hash items 32 bytes wide, an eight-byte checksum, and
    /// the well-known checksum key. Replicas within one trust domain share this; across domains a deployment
    /// supplies its own secret key.
    /// </summary>
    public static ReconciliationContract ContentHashDefault { get; } = new(ReconciliationItemDomain.ContentHash, 32, 8, WellKnownChecksumKeyLow, WellKnownChecksumKeyHigh);
}
