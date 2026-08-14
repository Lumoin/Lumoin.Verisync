using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the review finding that "nothing binds a recovered residual to the local
/// generation/epoch" and "no gate can tell that the peer sketch describes g' rather than g". The finding's
/// mechanism premise is that the checksum key is generation-independent, so two generations peel identically.
///
/// These probes exercise the seam the finding overlooks: the checksum key is a per-contract parameter, so a
/// deployment that keys per generation/epoch (exactly the binding the finding says is absent) makes the
/// decoder's own purity gate reject a cross-generation peer. The decoder recomputes purity under the LOCAL
/// contract key, so a peer stream keyed to a different generation cannot peel to completion.
///
/// The tests are permanent pins (repro tests are assets): they document that (a) within one key the decode is
/// the promised faithful symmetric-difference recovery, and (b) across two keys the peel fails to complete,
/// which is the generation gate the finding claims does not exist, plus (c) the offer handshake rejects the
/// mismatched key up front before any symbol flows.
/// </summary>
[TestClass]
internal sealed class PeerGenerationBindingProbeTests
{
    private const ulong GenerationKeyGLow = ReconciliationContract.WellKnownChecksumKeyLow;
    private const ulong GenerationKeyGHigh = ReconciliationContract.WellKnownChecksumKeyHigh;

    /// <summary>
    /// A distinct 128-bit key modelling a different local generation/epoch g'.
    /// </summary>
    /// <remarks>
    /// Any value != (g-low, g-high).
    /// </remarks>
    private const ulong GenerationKeyGPrimeLow = 0x1122334455667788UL;
    private const ulong GenerationKeyGPrimeHigh = 0x99AABBCCDDEEFF00UL;

    private static ReconciliationContract ContractG { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, GenerationKeyGLow, GenerationKeyGHigh);

    private static ReconciliationContract ContractGPrime { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, GenerationKeyGPrimeLow, GenerationKeyGPrimeHigh);


    [TestMethod]
    public void SameGenerationKeyPeelsToExactlyTheSymmetricDifference()
    {
        //One generation: both peers key under g. This is the promised faithful recovery, not a defect.
        byte[][] shared = Items(1L, 6);
        byte[][] leftOnly = Items(1_000_001L, 3);
        byte[][] rightOnly = Items(2_000_001L, 4);

        using ReconciliationEncoder left = Encoder(ContractG, [.. shared, .. leftOnly]);
        using ReconciliationEncoder right = Encoder(ContractG, [.. shared, .. rightOnly]);
        using ReconciliationDecoder decoder = new(ContractG, BaseMemoryPool.Shared);

        int d = leftOnly.Length + rightOnly.Length;
        int cap = 200 + (20 * d);
        int absorbed = 0;
        while(!decoder.IsComplete && absorbed < cap)
        {
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            absorbed++;
        }

        Assert.IsTrue(decoder.IsComplete, "A same-generation reconciliation must complete.");

        string[] expected = [.. Hex([.. leftOnly, .. rightOnly])];
        string[] decoded = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
        Assert.AreSequenceEqual(expected, decoded);
    }


    [TestMethod]
    public void CrossGenerationKeyOnIdenticalContentFailsToComplete()
    {
        //The sharpest case: identical content, only the generation key differs. If the checksum key were
        //truly generation-independent (the finding's premise) this would complete quiescent on symbol one,
        //indistinguishable from a same-generation equal-set reconcile. It does not: the peer keyed under g'
        //never peels against a decoder keyed under g, so the gate fires exactly on the generation mismatch.
        byte[][] content = Items(1L, 8);

        using ReconciliationEncoder local = Encoder(ContractG, content);
        using ReconciliationEncoder peer = Encoder(ContractGPrime, content);
        using ReconciliationDecoder decoder = new(ContractG, BaseMemoryPool.Shared);

        int cap = 500;
        int absorbed = 0;
        while(!decoder.IsComplete && absorbed < cap)
        {
            decoder.Absorb(local.ProduceNext().Combine(peer.ProduceNext()));
            absorbed++;
        }

        Assert.IsFalse(decoder.IsComplete, "A cross-generation-key peer must not peel to completion.");
    }


    [TestMethod]
    public void CrossGenerationKeyOnOverlappingSetsFailsToComplete()
    {
        byte[][] shared = Items(1L, 6);
        byte[][] leftOnly = Items(1_000_001L, 3);
        byte[][] rightOnly = Items(2_000_001L, 4);

        using ReconciliationEncoder local = Encoder(ContractG, [.. shared, .. leftOnly]);
        using ReconciliationEncoder peer = Encoder(ContractGPrime, [.. shared, .. rightOnly]);
        using ReconciliationDecoder decoder = new(ContractG, BaseMemoryPool.Shared);

        int cap = 500;
        int absorbed = 0;
        while(!decoder.IsComplete && absorbed < cap)
        {
            decoder.Absorb(local.ProduceNext().Combine(peer.ProduceNext()));
            absorbed++;
        }

        Assert.IsFalse(decoder.IsComplete, "A cross-generation-key peer must not peel to completion over overlapping sets.");
    }


    [TestMethod]
    public void OfferHandshakeRejectsCrossGenerationKeyBeforeAnySymbolFlows()
    {
        //The front-line gate: the offer's key-check tag differs across generations, so the session aborts up
        //front. Same generation matches; different generation does not.
        Assert.IsTrue(ReconciliationOffer.FromContract(ContractG).Matches(ContractG), "Same-generation offer must match.");
        Assert.IsFalse(ReconciliationOffer.FromContract(ContractGPrime).Matches(ContractG), "Cross-generation offer must not match.");
        Assert.IsFalse(ReconciliationOffer.FromContract(ContractG).Matches(ContractGPrime), "Cross-generation offer must not match (symmetric).");
    }


    private static ReconciliationEncoder Encoder(ReconciliationContract contract, byte[][] items)
    {
        ReconciliationEncoder encoder = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(byte[] item in items)
        {
            encoder.Add(item);
        }

        return encoder;
    }


    private static byte[][] Items(long baseSeed, int count)
    {
        byte[][] items = new byte[count][];
        for(int i = 0; i < count; i++)
        {
            items[i] = BitConverter.GetBytes(baseSeed + i);
        }

        return items;
    }


    private static IEnumerable<string> Hex(byte[][] items)
    {
        return items.Select(Convert.ToHexString).Order();
    }
}
