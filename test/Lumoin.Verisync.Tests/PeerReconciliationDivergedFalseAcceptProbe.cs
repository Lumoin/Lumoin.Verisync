using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the consumer-seam finding that the peer-reconciliation rung false-accepts a
/// diverged/cross-generation peer. It reproduces the finding's exact sketch against the real Verisync
/// encoder/decoder to establish, empirically, the Verisync-side facts the finding depends on:
///
///   Full pre-loss set F = {a, b, c, d} at generation g, lost block B0 = {d}.
///   Disk loss removes B0, so survivors S = {a, b, c}, lost.ItemCount = |B0| = 1.
///   Peer is a legitimately diverged replica in the SAME trust domain at generation g' where d was
///   retracted and e inserted: P = {a, b, c, e}.
///
/// The probe verifies that Recover(S, P) decodes exactly the symmetric difference {e}, that the three
/// Veritas gates (IsComplete, RecoveredCount == lost.ItemCount, RecoveredItemsArePeerOnly) all pass on
/// that decode, that the truly-lost d is silently dropped, and that the only Verisync reject signal
/// (checksum-key disagreement) does NOT fire for a same-domain diverged peer because it shares the key.
/// </summary>
[TestClass]
internal sealed class PeerReconciliationDivergedFalseAcceptProbe
{
    private static ReconciliationContract SameDomainContract { get; } = ReconciliationContract.ContentHashDefault;

    private static byte[] HashA { get; } = Digest("a");
    private static byte[] HashB { get; } = Digest("b");
    private static byte[] HashC { get; } = Digest("c");
    private static byte[] HashD { get; } = Digest("d");
    private static byte[] HashE { get; } = Digest("e");


    [TestMethod]
    public void DivergedSameDomainPeerFalseAcceptsAndHealsCorrupt()
    {
        byte[][] fullPreLoss = [HashA, HashB, HashC, HashD];
        byte[][] survivors = [HashA, HashB, HashC];
        byte[][] peer = [HashA, HashB, HashC, HashE];

        //The truly-lost block content L; its item count is what the Veritas rung knows and gates against.
        byte[][] lostBlock = [HashD];
        int lostItemCount = lostBlock.Length;

        //Recover the symmetric difference S (triangle) P through the real encoder/decoder pair.
        IReadOnlyList<ReadOnlyMemory<byte>> recovered = Reconcile(SameDomainContract, survivors, peer, out bool isComplete);

        //Gate A (line 282, first clause): the decode is complete.
        Assert.IsTrue(isComplete, "The decoder reports the difference fully recovered.");

        //Gate A (line 282, second clause): RecoveredCount == lost.ItemCount. Here |S triangle P| = |{e}| = 1 == 1.
        Assert.HasCount(lostItemCount, recovered, "The recovered count equals the lost-block item count, so the count gate passes.");

        //Gate B (line 296): RecoveredItemsArePeerOnly — every recovered item is absent from the survivors S.
        HashSet<string> survivorHex = [.. survivors.Select(h => ToHex(h))];
        bool recoveredItemsArePeerOnly = recovered.All(item => !survivorHex.Contains(ToHex(item.Span)));
        Assert.IsTrue(recoveredItemsArePeerOnly, "The recovered item e is peer-only (not in the survivors), so the peer-only gate passes.");

        //All three gates pass, yet the recovered residual is NOT the truly-lost block L.
        //The single recovered item is e (the peer's foreign insertion), not d (the truly-lost triple).
        string recoveredHex = ToHex(recovered[0].Span);
        Assert.AreEqual(ToHex(HashE), recoveredHex, "The rung recovers the peer's foreign item e, not the lost item d.");
        Assert.AreNotEqual(ToHex(HashD), recoveredHex, "The truly-lost item d is NOT what the difference yields.");

        //The heal publishes survivors union recovered-peer-only = {a, b, c, e}.
        HashSet<string> healed = [.. survivors.Select(h => ToHex(h))];
        foreach(ReadOnlyMemory<byte> item in recovered)
        {
            healed.Add(ToHex(item.Span));
        }

        HashSet<string> trueFull = [.. fullPreLoss.Select(h => ToHex(h))];

        //The healed system-of-record is corrupt: it drops the truly-lost d and inserts a foreign e.
        bool healedMatchesTruth = healed.SetEquals(trueFull);
        bool healedDroppedD = !healed.Contains(ToHex(HashD));
        bool healedInsertedForeignE = healed.Contains(ToHex(HashE));
        Assert.IsFalse(healedMatchesTruth, "The healed set is NOT the true pre-loss set: the heal is corrupt.");
        Assert.IsTrue(healedDroppedD, "The truly-lost item d is durably dropped by the heal.");
        Assert.IsTrue(healedInsertedForeignE, "A foreign item e is durably inserted by the heal.");
    }


    [TestMethod]
    public void KeyCheckDoesNotRejectASameDomainDivergedPeer()
    {
        //The only Verisync-side reject signal is checksum-key disagreement, surfaced through the offer's
        //KeyCheck. A same-domain diverged peer shares the well-known key, so the KeyCheck MATCHES and the
        //session proceeds — the guard cannot see the generation divergence.
        ReconciliationOffer peerOffer = ReconciliationOffer.FromContract(SameDomainContract);
        Assert.IsTrue(peerOffer.Matches(SameDomainContract), "A same-domain peer's offer matches: the key-check guard does not fire on a generation divergence.");
    }


    [TestMethod]
    public void CrossKeyPeerIsTheOnlyThingVerisyncRejects()
    {
        //Negative control: only a KEY difference (a cross-domain / per-epoch secret key) makes the decode
        //fail to complete. Encode the peer under a different key, decode under the survivors' key: the shared
        //items a, b, c never cancel (their checksum contributions differ under different keys), so no cell
        //reaches purity and IsComplete stays false. This is a KEY signal, not a generation/epoch signal, so
        //it does nothing for the same-domain diverged peer above.
        ReconciliationContract survivorKey = ReconciliationContract.ContentHashDefault;
        ReconciliationContract peerSecretKey = new(ReconciliationItemDomain.ContentHash, 32, 8, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

        byte[][] survivors = [HashA, HashB, HashC];
        byte[][] peer = [HashA, HashB, HashC, HashE];

        using ReconciliationEncoder left = LoadEncoder(survivorKey, survivors);
        using ReconciliationEncoder right = LoadEncoder(peerSecretKey, peer);
        using ReconciliationDecoder decoder = new(survivorKey, BaseMemoryPool.Shared);

        for(int n = 0; n < 64 && !decoder.IsComplete; n++)
        {
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        }

        Assert.IsFalse(decoder.IsComplete, "A cross-key peer's difference never completes: the key is the only reject signal Verisync has.");

        //And the offers do not match, so a keyed deployment would abort up front.
        Assert.IsFalse(ReconciliationOffer.FromContract(peerSecretKey).Matches(survivorKey), "Cross-key offers do not match; this is the guard that a same-domain divergence evades.");
    }


    private static IReadOnlyList<ReadOnlyMemory<byte>> Reconcile(ReconciliationContract contract, byte[][] leftItems, byte[][] rightItems, out bool isComplete)
    {
        using ReconciliationEncoder left = LoadEncoder(contract, leftItems);
        using ReconciliationEncoder right = LoadEncoder(contract, rightItems);
        using ReconciliationDecoder decoder = new(contract, BaseMemoryPool.Shared);

        int cap = 100 + (20 * (leftItems.Length + rightItems.Length));
        for(int n = 0; n < cap && !decoder.IsComplete; n++)
        {
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        }

        isComplete = decoder.IsComplete;

        return decoder.DecodedItems;
    }


    private static ReconciliationEncoder LoadEncoder(ReconciliationContract contract, byte[][] items)
    {
        ReconciliationEncoder encoder = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(byte[] item in items)
        {
            encoder.Add(item);
        }

        return encoder;
    }


    private static byte[] Digest(string element)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(element));
    }


    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes);
    }
}
