using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial probe for the consumer-seam finding that "the repair sketch decodes under the PUBLIC well-known
/// checksum key, so the purity gate authenticates nothing, and a dishonest peer crafts a sketch that peels to
/// attacker-chosen items which the coordinator then writes into the durable system-of-record."
///
/// The finding's reproSketch limits its gate analysis to RepairCoordinator.cs:282-299 and never engages the
/// faithfulness gate the coordinator applies before any heal is staged: HealedSetMatchesGenerationSketch
/// (RepairCoordinator.cs:336, defined 439-470), which reconciles the healed set (survivors + recovered) against
/// the GENERATION'S OWN at-rest-verified sketch — a LOCAL record of the exact pre-damage item set that a
/// dishonest peer neither supplies nor knows the content of.
///
/// This probe reproduces the attack against the REAL Verisync encoder/decoder under the PUBLIC well-known key
/// (ReconciliationContract.ContentHashDefault) to establish, empirically, the Verisync-side facts:
///
///   Pre-damage set D = {a, b, c, d} at the damaged generation; lost block B0 = {d}.
///   Disk loss removes B0, so survivors S = {a, b, c}, lost.ItemCount = |B0| = 1.
///   A dishonest peer, knowing the PUBLIC key, crafts peer set P = S union {x} for any attacker-chosen x != d.
///
/// Round 1 (RepairCoordinator gates A/B): Recover(S, P) peels to exactly {x}, IsComplete, count == 1, peer-only
/// — every early gate the finding names passes, exactly as it claims.
///
/// Round 2 (the faithfulness gate the finding omits): the healed set S union {x} = {a, b, c, x} is reconciled
/// against the generation's own record D = {a, b, c, d}. Because x != d, the residual is {x, d} != empty, so the
/// gate DECLINES to a named loss instead of writing attacker-chosen content. Knowing the public key does not help
/// the attacker here: the residual is computed over two LOCAL sketches (the healed set and the generation record),
/// and no attacker-chosen x other than the genuine d makes {a, b, c, x} reconcile emptily against {a, b, c, d}.
/// The control (x == d, i.e. the genuine lost item) is the only heal that passes — which is the faithful restore.
/// </summary>
[TestClass]
internal sealed class PeerReconciliationPublicKeyChosenContentGateProbe
{
    //ContentHashDefault pins the PUBLIC WellKnownChecksumKeyLow/High — exactly the "public key" the finding names.
    private static ReconciliationContract PublicKeyContract { get; } = ReconciliationContract.ContentHashDefault;

    private static byte[] HashA { get; } = Digest("a");
    private static byte[] HashB { get; } = Digest("b");
    private static byte[] HashC { get; } = Digest("c");
    private static byte[] HashD { get; } = Digest("d");


    [TestMethod]
    public void ChosenContentPeelsUnderPublicKeyButFailsTheFaithfulnessGate()
    {
        byte[][] survivors = [HashA, HashB, HashC];
        byte[][] genuineLost = [HashD];
        byte[][] preDamage = [HashA, HashB, HashC, HashD];

        //A dishonest peer that knows the public key crafts an attacker-chosen item x != d and a peer set that
        //peels, against the survivors, to exactly {x}. Any forgeable 32-byte content the attacker likes.
        byte[] attackerChosen = Digest("attacker-chosen-poison");
        byte[][] peer = [HashA, HashB, HashC, attackerChosen];

        //Round 1: the early gates the finding names (IsComplete, count == lost.ItemCount, peer-only) all pass.
        IReadOnlyList<ReadOnlyMemory<byte>> recovered = Reconcile(PublicKeyContract, survivors, peer, out bool round1Complete);
        Assert.IsTrue(round1Complete, "Under the public key the attacker's crafted difference peels to completion (gate A first clause).");
        Assert.HasCount(genuineLost.Length, recovered, "The recovered count equals the lost-block item count (gate A second clause).");

        HashSet<string> survivorHex = [.. survivors.Select(static s => ToHex(s))];
        bool peerOnly = recovered.All(item => !survivorHex.Contains(ToHex(item.Span)));
        Assert.IsTrue(peerOnly, "The recovered attacker item is peer-only (gate B passes).");
        Assert.AreEqual(ToHex(attackerChosen), ToHex(recovered[0].Span), "Round 1 recovers exactly the attacker-chosen item, not the genuine lost d.");

        //Round 2 — the faithfulness gate the finding omits (HealedSetMatchesGenerationSketch): reconcile the
        //healed set {a, b, c, attackerChosen} against the generation's own at-rest record D = {a, b, c, d}.
        byte[][] attackerHealed = [HashA, HashB, HashC, attackerChosen];
        (bool residualComplete, int residualCount) = Residual(PublicKeyContract, attackerHealed, preDamage);

        //The gate accepts ONLY a complete peel of ZERO residual items. The attacker's heal fails it: the residual
        //is {attackerChosen, d}, so the coordinator DECLINES to a named loss instead of writing poisoned content.
        bool faithfulnessGatePasses = residualComplete && residualCount == 0;
        Assert.IsFalse(faithfulnessGatePasses, "The attacker-chosen heal fails the faithfulness gate: it is NOT written to the system-of-record.");
        Assert.AreEqual(2, residualCount, "The residual against the generation's own record is exactly {attackerChosen, d} — non-empty, so the heal is rejected.");
    }


    [TestMethod]
    public void OnlyTheGenuineLostItemPassesTheFaithfulnessGate()
    {
        byte[][] survivors = [HashA, HashB, HashC];
        byte[][] preDamage = [HashA, HashB, HashC, HashD];

        //Control: a faithful heal supplies the genuine lost item d, so the healed set equals the pre-damage set.
        byte[][] faithfulHealed = [HashA, HashB, HashC, HashD];
        (bool residualComplete, int residualCount) = Residual(PublicKeyContract, faithfulHealed, preDamage);

        Assert.IsTrue(residualComplete, "A faithful heal reconciles to a complete peel against the generation's own record.");
        Assert.AreEqual(0, residualCount, "A faithful heal has an EMPTY residual — the only case the gate accepts.");
    }


    [TestMethod]
    public void NoAttackerChoiceOtherThanTheGenuineItemSurvivesTheGate()
    {
        //Knowing the public key buys the attacker nothing at the faithfulness gate: sweep many attacker-chosen
        //substitutions and confirm every x != d yields a non-empty residual against the generation's own record,
        //so none is ever written. The genuine d is the sole heal that reconciles emptily.
        byte[][] survivors = [HashA, HashB, HashC];
        byte[][] preDamage = [HashA, HashB, HashC, HashD];

        for(int i = 0; i < 64; i++)
        {
            byte[] chosen = Digest($"poison-{i}");
            if(ToHex(chosen) == ToHex(HashD))
            {
                //Astronomically impossible for SHA-256, but skip if a chosen value ever equals the genuine item.
                continue;
            }

            byte[][] healed = [HashA, HashB, HashC, chosen];
            (bool residualComplete, int residualCount) = Residual(PublicKeyContract, healed, preDamage);
            bool gatePasses = residualComplete && residualCount == 0;
            Assert.IsFalse(gatePasses, $"Attacker choice #{i} must not pass the faithfulness gate under the public key.");
        }
    }


    //Models RepairCoordinator round 1: recover the symmetric difference of the survivors and the peer set.
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


    //Models RepairCoordinator.HealedSetMatchesGenerationSketch: reconcile the healed set against the
    //generation's own at-rest record and report the residual completeness and count.
    private static (bool IsComplete, int RecoveredCount) Residual(ReconciliationContract contract, byte[][] healed, byte[][] generationRecord)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> residual = Reconcile(contract, healed, generationRecord, out bool complete);

        return (complete, residual.Count);
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
