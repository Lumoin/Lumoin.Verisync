using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The seal protocol of <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> over the
/// base-materializing offset strategy, whose public addressing type is <see cref="OffsetAddress"/>.
/// Every seal happens at a FULL-CONTEXT (insert-quiescent)
/// frontier except where the test's point is the refusal: sealing an offset document is a
/// group-quiescent checkpoint, and a straggling writer above the committed frontier recovers by
/// wholesale adoption plus container merge, verified by the NEXT committed seal. Two offset
/// load-bearing readings apply throughout: expected projections and checkpoints are computed from
/// PRE-seal states — a base-changing compaction re-identifies converted elements as base sentinels,
/// so a post-seal projection can never be the comparand — and any adoption donor whose digest the
/// CURRENT commitment must verify has NOT yet compacted at the committed frontier. Convergence is
/// asserted component-wise — live sequence, dotted checkpoint, commitment — never through full
/// container equality.
/// </summary>
[TestClass]
internal sealed class OffsetCheckpointSealTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R3 { get; } = Replica(3);
    private static ReplicaId RA { get; } = Replica(10);
    private static ReplicaId RB { get; } = Replica(20);


    /// <summary>
    /// A won seal commits its commitment, compacts the live sequence at the frontier, and records the dotted
    /// certified projection computed from the PRE-seal live.
    /// </summary>
    [TestMethod]
    public void ASealCommitsCompactsAndRecordsTheDottedCheckpoint()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withB, OffsetAddress anchorB) = withA.InsertAfter(anchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> removed = withB.Remove(anchorB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        //The comparand comes from the PRE-seal live: after the seal the conversion has re-keyed the
        //surviving element onto a base sentinel, so the post-seal live's projection could never match.
        ImmutableArray<SequenceCheckpointEntry<string>> expectedProjection = removed.Live.CertifiedProjection(frontier);

        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> afterSeal, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            removed.Seal(register, new Ballot(1, RA), frontier);

        Assert.IsTrue(wasSealed);
        Assert.IsTrue(outcome.IsChosen);
        Assert.AreSequenceEqual(expectedProjection.ToArray(), afterSeal.Checkpoint.ToArray());
        Assert.HasCount(1, afterSeal.Checkpoint);
        Assert.AreEqual("a", afterSeal.Checkpoint[0].Value);

        //The commitment is keyed to exactly the sealed frontier.
        Assert.AreEqual(frontier, afterSeal.Commitment!.Frontier);

        //The live sequence is compacted: a converts to base slot 0 advancing to generation 1, b's
        //certified remove drops the vertex, and the dropped anchor still translates — to the gap where it
        //sat, a current-generation base address.
        string[] expectedValues = ["a"];
        Assert.AreSequenceEqual(expectedValues, afterSeal.Values.ToArray());
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 1), afterSeal.Live.TranslateAnchor(anchorB));
    }


    /// <summary>
    /// A non-sealer over the byte-identical full state applies the committed seal and converges component-wise
    /// — the determinism theorem end to end over the base-materializing conversion.
    /// </summary>
    [TestMethod]
    public void ANonSealerAppliesTheCommittedSealAndConverges()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withB, OffsetAddress anchorB) = withA.InsertAfter(anchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> removed = withB.Remove(anchorB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> sealer, _, ChangeOutcome<CheckpointCommitment> outcome, _) =
            removed.Seal(register, ballot, frontier);

        //A second container over the byte-identical full state applies the committed commitment.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> otherA, OffsetAddress otherAnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> otherB, OffsetAddress otherAnchorB) = otherA.InsertAfter(otherAnchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> otherRemoved = otherB.Remove(otherAnchorB, R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> applied = otherRemoved.ApplyCommittedSeal(outcome.Value!, ballot);

        Assert.AreEqual(sealer.Live, applied.Live);
        Assert.AreSequenceEqual(sealer.Checkpoint.ToArray(), applied.Checkpoint.ToArray());
        Assert.AreEqual(sealer.Commitment, applied.Commitment);
    }


    /// <summary>
    /// A competing seal that is behind aborts unchanged, re-proposing the winner's commitment; a later seal
    /// that strictly dominates the winner succeeds.
    /// </summary>
    /// <remarks>
    /// Every offset seal frontier must be the sealer's own full context, so the behind sealer holds ONLY the
    /// first edit at its F0 attempt and applies the remaining edits AFTER the abort, sealing at F2 from its own
    /// grown full context.
    /// </remarks>
    [TestMethod]
    public void ACompetingSealAbortsUnchangedAndSucceedsAboveTheWinner()
    {
        //Sealer A holds a and b; F1 is its own full context.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> aWithA, OffsetAddress aAnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> aWithB, _) = aWithA.InsertAfter(aAnchorA, "b", R1);
        VectorClock f1 = aWithB.CausalContext!;

        //Sealer B holds only the byte-identical first edit; F0 is its full context at the attempt.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> bWithA, OffsetAddress bAnchorA) = Sealable().InsertAtHead("a", R1);
        VectorClock f0 = bWithA.CausalContext!;

        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> sealedA, CasPaxosRegister<CheckpointCommitment> registerAfterA, _, bool aSealed) =
            aWithB.Seal(register, new Ballot(1, RA), f1);
        Assert.IsTrue(aSealed);

        //B seals at F0, which A's F1 strictly dominates, under a higher ballot: the refusal arm
        //re-proposes A's commitment, so B does not seal and its container is returned unchanged.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> sealedB, CasPaxosRegister<CheckpointCommitment> registerAfterB, ChangeOutcome<CheckpointCommitment> outcomeB, bool bSealed) =
            bWithA.Seal(registerAfterA, new Ballot(2, RB), f0);
        Assert.IsFalse(bSealed);
        Assert.AreEqual(bWithA, sealedB);
        Assert.AreEqual(sealedA.Commitment, outcomeB.Value);

        //Pinned component-wise too: nothing was recorded and nothing compacted.
        Assert.IsNull(sealedB.Commitment);
        Assert.IsNull(sealedB.CheckpointBallot);
        Assert.HasCount(0, sealedB.Checkpoint);
        Assert.AreEqual(bWithA.Live, sealedB.Live);

        //B applies the remaining edits after the abort and seals at F2 — its own grown full context —
        //which strictly dominates F1: the chain ascends and the seal succeeds.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> bWithB, OffsetAddress bAnchorB) = sealedB.InsertAfter(bAnchorA, "b", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> bWithC, _) = bWithB.InsertAfter(bAnchorB, "c", R1);
        VectorClock f2 = bWithC.CausalContext!;
        (_, _, ChangeOutcome<CheckpointCommitment> outcomeB2, bool bSealed2) = bWithC.Seal(registerAfterB, new Ballot(3, RB), f2);
        Assert.IsTrue(bSealed2);
        Assert.IsTrue(outcomeB2.IsChosen);
    }


    /// <summary>
    /// Two honest sealers at one frontier propose byte-identical digests, so the equal-frontier re-seal must
    /// come from a second member that has NOT yet applied or compacted: it hits the equal arm under a higher
    /// ballot and only the ballot advances.
    /// </summary>
    /// <remarks>
    /// The discontinuity pin: the FIRST sealer re-sealing from its own now-COMPACTED container reaches the
    /// equal-frontier-divergent-digest refusal — the conversion re-keyed its projection onto base sentinels —
    /// and aborts unchanged.
    /// </remarks>
    [TestMethod]
    public void AnEqualFrontierResealIsIdempotentFromAnUncompactedPeer()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithA, OffsetAddress m1AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithB, _) = m1WithA.InsertAfter(m1AnchorA, "b", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithA, OffsetAddress m2AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithB, _) = m2WithA.InsertAfter(m2AnchorA, "b", R1);

        VectorClock frontier = m1WithB.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> first, CasPaxosRegister<CheckpointCommitment> registerAfterFirst, _, bool firstSealed) =
            m1WithB.Seal(register, new Ballot(1, RA), frontier);
        Assert.IsTrue(firstSealed);

        //The uncompacted peer's projection still carries the real dots, so the digests coincide: the
        //equal arm seals again and only the recorded ballot advances.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> second, CasPaxosRegister<CheckpointCommitment> registerAfterSecond, _, bool secondSealed) =
            m2WithB.Seal(registerAfterFirst, new Ballot(2, RB), frontier);
        Assert.IsTrue(secondSealed);
        Assert.AreEqual(first.Live, second.Live);
        Assert.AreSequenceEqual(first.Checkpoint.ToArray(), second.Checkpoint.ToArray());
        Assert.AreEqual(first.Commitment, second.Commitment);
        Assert.AreEqual(new Ballot(2, RB), second.CheckpointBallot);

        //The compacted re-seal is refused: the first sealer's projection at the same frontier is now
        //sentinel-re-keyed, the digests diverge at the equal frontier, and the seal aborts unchanged.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> resealed, _, _, bool resealedSealed) =
            first.Seal(registerAfterSecond, new Ballot(3, RA), frontier);
        Assert.IsFalse(resealedSealed);
        Assert.AreEqual(first, resealed);
    }


    /// <summary>
    /// Committed seals apply in chain order: a stale earlier commitment is rejected by frontier order even when
    /// its digest coincides with the applier's projection at that earlier frontier.
    /// </summary>
    /// <remarks>
    /// Both commitments sit at full-context frontiers of their stages.
    /// </remarks>
    [TestMethod]
    public void ApplyingAStaleEarlierSealFailsClosedByChainOrder()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        VectorClock f1 = withA.CausalContext!;
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withB, _) = withA.InsertAfter(anchorA, "b", R1);
        VectorClock f2 = withB.CausalContext!;

        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> afterSeal, _, _, bool wasSealed) =
            withB.Seal(register, new Ballot(1, RA), f2);
        Assert.IsTrue(wasSealed);

        //The stale commitment's digest coincides with the applier's own projection at the earlier
        //stage's full context, so only the chain-order guard can reject it.
        var staleCommitted = new CheckpointCommitment(f1, Sha256(Canonicalize(afterSeal.Live.CertifiedProjection(f1))));

        Assert.ThrowsExactly<InvalidOperationException>(() => afterSeal.ApplyCommittedSeal(staleCommitted, new Ballot(2, RA)));
    }


    /// <summary>
    /// Applying a committed seal whose digest disagrees with the applier's own certified projection at the
    /// committed frontier fails closed — the "y" container applying the "x" commitment.
    /// </summary>
    [TestMethod]
    public void ApplyCommittedSealFailsClosedOnADivergentDigest()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> containerX, _) = Sealable().InsertAtHead("x", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> containerY, _) = Sealable().InsertAtHead("y", R1);

        VectorClock frontier = containerX.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (_, _, ChangeOutcome<CheckpointCommitment> outcomeX, _) = containerX.Seal(register, ballot, frontier);

        Assert.ThrowsExactly<InvalidOperationException>(() => containerY.ApplyCommittedSeal(outcomeX.Value!, ballot));
    }


    /// <summary>
    /// The adoption recipe with the donor pinned PRE-APPLY: the digest-verification adoption check is valid
    /// exactly for donors still on the commitment's SOURCE generation, so the rejoiner adopts the non-applied
    /// member's live state and applies the committed seal; everything converges once the donor itself applies
    /// too.
    /// </summary>
    [TestMethod]
    public void ARejoinerAdoptsADonorStateAndAppliesTheCommittedSeal()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithA, OffsetAddress m1AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithB, OffsetAddress m1AnchorB) = m1WithA.InsertAfter(m1AnchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Removed = m1WithB.Remove(m1AnchorB, R1);

        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithA, OffsetAddress m2AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithB, OffsetAddress m2AnchorB) = m2WithA.InsertAfter(m2AnchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Removed = m2WithB.Remove(m2AnchorB, R1);

        VectorClock frontier = m1Removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> sealer, _, ChangeOutcome<CheckpointCommitment> outcome, _) =
            m1Removed.Seal(register, ballot, frontier);

        //The donor m2 has NOT yet applied: its live is uncompacted, still on the source generation.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> rejoiner =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Adopt(
                WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256, m2Removed.Live);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> applied = rejoiner.ApplyCommittedSeal(outcome.Value!, ballot);

        //Component-wise convergence with the sealer, and with the donor once it applies too.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Applied = m2Removed.ApplyCommittedSeal(outcome.Value!, ballot);
        Assert.AreEqual(sealer.Live, applied.Live);
        Assert.AreEqual(m2Applied.Live, applied.Live);
        Assert.AreSequenceEqual(sealer.Checkpoint.ToArray(), applied.Checkpoint.ToArray());
        Assert.AreEqual(sealer.Commitment, applied.Commitment);
        Assert.AreEqual(m2Applied.Commitment, applied.Commitment);

        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Adopt(
            WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256, null!));
    }


    /// <summary>
    /// The inverted arm: on the base-materializing strategy a seal with unstable edits present THROWS — the RGA
    /// excludes-and-seals shape does not exist here.
    /// </summary>
    /// <remarks>
    /// The refusal is pre-consensus: a subsequent quiescent seal at the grown full context on the SAME register
    /// instance is chosen as the register's FIRST commitment, so nothing was promised or accepted by the failed
    /// attempt. The immutable register exposes no equality, so the observable is the first commitment's
    /// frontier.
    /// </remarks>
    [TestMethod]
    public void ASealWithUnstableEditsPresentFailsClosedBeforeAnyConsensusRound()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        VectorClock frontier = withA.CausalContext!;
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withB, _) = withA.InsertAfter(anchorA, "b", R1);

        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        //b's insert sits above the frontier, so the seal fails closed.
        Assert.ThrowsExactly<InvalidOperationException>(() => withB.Seal(register, new Ballot(1, RA), frontier));

        //The quiescent seal at the grown full context succeeds as the register's first commitment.
        VectorClock grown = withB.CausalContext!;
        (_, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) = withB.Seal(register, new Ballot(2, RA), grown);
        Assert.IsTrue(wasSealed);
        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual(grown, outcome.Value!.Frontier);
    }


    /// <summary>
    /// The straggler wedge end to end: a writer holding an insert above the committed frontier passes the
    /// dominance and digest checks yet fails the committed seal on the probe layer, and its honest recovery is
    /// wholesale adoption plus container merge, with verification arriving at the NEXT seal.
    /// </summary>
    /// <remarks>
    /// The regression pin holds the unrealizable-recipe shape forever: adopting a POST-seal donor and
    /// re-applying the CURRENT commitment fails the digest check by sentinel re-keying.
    /// </remarks>
    [TestMethod]
    public void AStragglingWriterFailsTheCommittedSealAndRecoversByAdoptionAndMerge()
    {
        //All three members hold the byte-identical prefix a=(R1,1), b=(R1,2); F is their common full context.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> shared, OffsetAddress anchorB) = withA.InsertAfter(anchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1 = shared;
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2 = shared;
        VectorClock frontier = shared.CausalContext!;

        //The straggler inserts c above F: from context {R1:2} the Lamport mint on R3 yields (R3,3).
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m3, OffsetAddress anchorC) = shared.InsertAfter(anchorB, "c", R3);
        Assert.AreEqual(new Dot(R3, 3), anchorC.Anchor.LiveId);

        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Sealed, CasPaxosRegister<CheckpointCommitment> registerAfterSeal, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            m1.Seal(register, ballot, frontier);
        Assert.IsTrue(wasSealed);

        //A quiescent member applies and converges.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Applied = m2.ApplyCommittedSeal(outcome.Value!, ballot);
        Assert.AreEqual(m1Sealed.Live, m2Applied.Live);
        Assert.AreEqual(m1Sealed.Commitment, m2Applied.Commitment);

        //The straggler's probe at F reports exactly c's dot, and the committed seal fails closed on the
        //probe layer: dominance and digest both pass, since the certified projection at F excludes c.
        Dot[] expectedGap = [new Dot(R3, 3)];
        ImmutableArray<Dot>? probe = m3.UnstableInserts(frontier);
        Assert.IsNotNull(probe);
        Assert.AreSequenceEqual(expectedGap, probe!.Value.ToArray());

        //The two upstream checks provably pass, so the throw below can only be the probe layer: the
        //straggler's context strictly dominates F, and its own canonicalized certified projection at F is
        //byte-equal to the committed digest.
        Assert.AreEqual(Causality.After, m3.CausalContext!.Compare(frontier));
        ReadOnlyMemory<byte> stragglerDigest = Sha256(Canonicalize(m3.Live.CertifiedProjection(frontier)));
        Assert.AreSequenceEqual(stragglerDigest.ToArray(), outcome.Value!.Digest.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => m3.ApplyCommittedSeal(outcome.Value!, ballot));

        //THE REGRESSION PIN: the adopted POST-seal donor's projection at F is sentinel-keyed while the
        //commitment's digest was computed over real dots, so re-applying the CURRENT commitment throws
        //on the digest — the shipped rejoin recipe is unrealizable after a base-changing seal.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> adopted =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Adopt(
                WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256, m1Sealed.Live);
        Assert.ThrowsExactly<InvalidOperationException>(() => adopted.ApplyCommittedSeal(outcome.Value!, ballot));

        //THE HONEST RECOVERY: the container merge's higher-ballot arm hands checkpoint, commitment,
        //and ballot over to the adopter.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> inherited = adopted.Merge(m1Sealed);
        Assert.AreEqual(m1Sealed.Commitment, inherited.Commitment);
        Assert.AreEqual(m1Sealed.CheckpointBallot, inherited.CheckpointBallot);
        Assert.AreSequenceEqual(m1Sealed.Checkpoint.ToArray(), inherited.Checkpoint.ToArray());

        //The adopted state holds NO vertex for c: the straggler's own edit did not survive adoption.
        Assert.IsNull(inherited.Live.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtLive(new Dot(R3, 3)), 0)));
        string[] adoptedValues = ["a", "b"];
        Assert.AreSequenceEqual(adoptedValues, inherited.Values.ToArray());

        //Re-inserting c's value from context {R1:2} re-mints the SAME dot (R3,3) — the deterministic
        //rebirth is harmless, because every holder of an uncovered vertex must itself adopt.
        OffsetAddress? rebirthTarget = inherited.Live.TranslateAnchor(anchorB);
        Assert.IsNotNull(rebirthTarget);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> recovered, OffsetAddress reborn) = inherited.InsertAfter(rebirthTarget!, "c", R3);
        Assert.AreEqual(new Dot(R3, 3), reborn.Anchor.LiveId);

        //All members converge on the visible values once merged.
        string[] converged = ["a", "b", "c"];
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Gossiped = m1Sealed.Merge(recovered);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Gossiped = m2Applied.Merge(recovered);
        Assert.AreSequenceEqual(converged, m1Gossiped.Values.ToArray());
        Assert.AreSequenceEqual(converged, m2Gossiped.Values.ToArray());
        Assert.AreSequenceEqual(converged, recovered.Merge(m1Sealed).Values.ToArray());

        //THE LIFECYCLE CLOSE: gossip covers c, the next seal lands strictly above F, and the recovered
        //straggler applies it — verified by the NEXT committed seal, as the recovery contract instructs.
        VectorClock nextFrontier = m1Gossiped.CausalContext!;
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1SealedNext, _, ChangeOutcome<CheckpointCommitment> outcomeNext, bool nextSealed) =
            m1Gossiped.Seal(registerAfterSeal, new Ballot(2, RA), nextFrontier);
        Assert.IsTrue(nextSealed);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> recoveredApplied =
            recovered.ApplyCommittedSeal(outcomeNext.Value!, new Ballot(2, RA));
        Assert.AreEqual(m1SealedNext.Live, recoveredApplied.Live);
        Assert.AreSequenceEqual(m1SealedNext.Checkpoint.ToArray(), recoveredApplied.Checkpoint.ToArray());
        Assert.AreEqual(m1SealedNext.Commitment, recoveredApplied.Commitment);
    }


    /// <summary>
    /// TWO independent recovery executions from the SAME donor re-mint the straggler's uncovered vertex
    /// divergently.
    /// </summary>
    /// <remarks>
    /// The dot rebirth is deterministic — c re-mints as (R3,3) from context {R1:2} no matter where it is placed
    /// — so one recovery placing c after b and another placing it after a carry the SAME dot with DIFFERENT
    /// vertices. Merging the two recovered containers fails closed on the equivocation detector in both orders
    /// rather than letting merge order silently pick a predecessor, while a single recovery gossiped onward
    /// merges cleanly, since every holder then carries the byte-identical vertex.
    /// </remarks>
    [TestMethod]
    public void ATwiceRunRecoveryReMintingTheDotDivergentlyFailsClosedOnMerge()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> shared, OffsetAddress anchorB) = withA.InsertAfter(anchorA, "b", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1 = shared;
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2 = shared;
        VectorClock frontier = shared.CausalContext!;

        //The straggler holds c above F; everyone else seals and applies without it.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m3, OffsetAddress anchorC) = shared.InsertAfter(anchorB, "c", R3);
        Assert.AreEqual(new Dot(R3, 3), anchorC.Anchor.LiveId);

        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Sealed, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            m1.Seal(register, ballot, frontier);
        Assert.IsTrue(wasSealed);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Applied = m2.ApplyCommittedSeal(outcome.Value!, ballot);

        //Both recoveries adopt M1's post-seal live and inherit the commitment by container merge; the adopted
        //state holds no vertex for c, so each re-inserts c's value afresh.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> inherited =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Adopt(
                WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256, m1Sealed.Live).Merge(m1Sealed);
        Assert.IsNull(inherited.Live.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtLive(new Dot(R3, 3)), 0)));

        //Recovery X places c after b, recovery Y after a: both re-mint the SAME (R3,3) from context {R1:2},
        //but the divergent predecessors make the two vertices unequal.
        OffsetAddress? afterB = inherited.Live.TranslateAnchor(anchorB);
        OffsetAddress? afterA = inherited.Live.TranslateAnchor(anchorA);
        Assert.IsNotNull(afterB);
        Assert.IsNotNull(afterA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> recoveredAfterB, OffsetAddress rebornB) = inherited.InsertAfter(afterB!, "c", R3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> recoveredAfterA, OffsetAddress rebornA) = inherited.InsertAfter(afterA!, "c", R3);
        Assert.AreEqual(new Dot(R3, 3), rebornB.Anchor.LiveId);
        Assert.AreEqual(new Dot(R3, 3), rebornA.Anchor.LiveId);

        //The two divergently-recovered containers fail closed on the equivocation detector in both orders.
        Assert.ThrowsExactly<InvalidOperationException>(() => recoveredAfterB.Merge(recoveredAfterA));
        Assert.ThrowsExactly<InvalidOperationException>(() => recoveredAfterA.Merge(recoveredAfterB));

        //A single recovery gossiped onward merges cleanly: an independent execution of the SAME after-b
        //recovery re-mints the byte-identical vertex, so the detector stays quiet and every holder converges.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> recoveredAfterBAgain, _) = inherited.InsertAfter(afterB!, "c", R3);
        string[] converged = ["a", "b", "c"];
        Assert.AreSequenceEqual(converged, recoveredAfterB.Merge(recoveredAfterBAgain).Values.ToArray());
        Assert.AreSequenceEqual(converged, recoveredAfterB.Merge(m1Sealed).Values.ToArray());
        Assert.AreSequenceEqual(converged, m2Applied.Merge(recoveredAfterB).Values.ToArray());
    }


    /// <summary>
    /// The container probe: the seal-readiness and apply-vs-adopt diagnostic.
    /// </summary>
    /// <remarks>
    /// It reports the uncovered insert-dots in (Replica, Counter) order for the offset strategy, and NULL for a
    /// strategy that leaves the seam unwired — hosts branch on the slot's presence to learn whether sealing is
    /// group-quiescent at all.
    /// </remarks>
    [TestMethod]
    public void TheContainerProbeReportsTheGapAndIsNullWithoutTheSeam()
    {
        //An empty offset container probes empty through the wired seam.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> empty = Sealable();
        ImmutableArray<Dot>? emptyProbe = empty.UnstableInserts(VectorClock.Empty);
        Assert.IsNotNull(emptyProbe);
        Assert.IsTrue(emptyProbe!.Value.IsEmpty);

        //With vertices on two axes the probe at the empty frontier lists every insert-dot sorted by
        //(Replica, Counter): R1's dots precede R3's by replica byte order, counters ascend within one.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, OffsetAddress anchorA) = empty.InsertAtHead("a", R3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withB, OffsetAddress anchorB) = withA.InsertAfter(anchorA, "b", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withC, _) = withB.InsertAfter(anchorB, "c", R1);
        Dot[] expected = [new Dot(R1, 2), new Dot(R1, 3), new Dot(R3, 1)];
        ImmutableArray<Dot>? gapProbe = withC.UnstableInserts(VectorClock.Empty);
        Assert.IsNotNull(gapProbe);
        Assert.AreSequenceEqual(expected, gapProbe!.Value.ToArray());

        //The member's own full context covers every insert, so the probe reads empty there.
        ImmutableArray<Dot>? coveredProbe = withC.UnstableInserts(withC.CausalContext!);
        Assert.IsNotNull(coveredProbe);
        Assert.IsTrue(coveredProbe!.Value.IsEmpty);

        //A strategy without the seam advertises null at any frontier.
        CheckpointedSequence<Rga<string>, string, Dot> rgaRle =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRgaRle<string>(), Canonicalize, Sha256);
        Assert.IsNull(rgaRle.UnstableInserts(VectorClock.Empty));
        (CheckpointedSequence<Rga<string>, string, Dot> rgaWithX, _) = rgaRle.InsertAtHead("x", R1);
        Assert.IsNull(rgaWithX.UnstableInserts(rgaWithX.CausalContext!));

        Assert.ThrowsExactly<ArgumentNullException>(() => withC.UnstableInserts(null!));
    }


    /// <summary>
    /// The apply-once pin behind the scoped idempotence doc: re-applying an already-applied seal passes chain
    /// order on the Equal frontier and throws on the digest, because the applied compaction re-keyed the
    /// projection onto base sentinels.
    /// </summary>
    /// <remarks>
    /// Offset appliers apply each committed seal exactly once, and the failure is fail-closed, not corruption.
    /// </remarks>
    [TestMethod]
    public void ReapplyingAnAppliedSealFailsClosedForABaseMaterializingStrategy()
    {
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithA, OffsetAddress m1AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithB, _) = m1WithA.InsertAfter(m1AnchorA, "b", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithA, OffsetAddress m2AnchorA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithB, _) = m2WithA.InsertAfter(m2AnchorA, "b", R1);

        VectorClock frontier = m1WithB.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (_, _, ChangeOutcome<CheckpointCommitment> outcome, _) = m1WithB.Seal(register, ballot, frontier);

        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> applied = m2WithB.ApplyCommittedSeal(outcome.Value!, ballot);

        Assert.ThrowsExactly<InvalidOperationException>(() => applied.ApplyCommittedSeal(outcome.Value!, ballot));
    }


    /// <summary>
    /// The apply-once pin is scoped to BASE-CHANGING seals: a DROP-ONLY offset seal does not re-key the
    /// projection onto base sentinels, so re-applying its commitment SUCCEEDS idempotently.
    /// </summary>
    /// <remarks>
    /// A first seal converts a into the base (base-changing); on that generation both members insert a
    /// childless x and remove it, and the second seal drops x without touching the base, so the applier's
    /// projection at the drop-only frontier — a's slot was already a sentinel — still matches the committed
    /// digest and the re-apply is idempotent, unlike the base-changing arm above. On the base generation the
    /// inserts name current-generation offset 0, so their addresses carry generation 1.
    /// </remarks>
    [TestMethod]
    public void ReapplyingADropOnlySealIsIdempotentForTheOffsetStrategy()
    {
        //A first base-changing seal converts a into the base, establishing generation one.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, _) = Sealable().InsertAtHead("a", R1);
        VectorClock f1 = withA.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot firstBallot = new(1, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Gen1, CasPaxosRegister<CheckpointCommitment> registerAfterF1, ChangeOutcome<CheckpointCommitment> outcomeF1, bool sealedF1) =
            withA.Seal(register, firstBallot, f1);
        Assert.IsTrue(sealedF1);

        //A second member applies the first seal, so both sit on the materialized base generation [a].
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> otherA, _) = Sealable().InsertAtHead("a", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2Gen1 = otherA.ApplyCommittedSeal(outcomeF1.Value!, firstBallot);

        //On the base generation both insert a childless x after a's slot and remove it — a drop-only edit set.
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1WithX, OffsetAddress x1) = m1Gen1.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 1), "x", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1XRemoved = m1WithX.Remove(x1, R1);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2WithX, OffsetAddress x2) = m2Gen1.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 1), "x", R1);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m2XRemoved = m2WithX.Remove(x2, R1);

        VectorClock f2 = m1XRemoved.CausalContext!;
        Ballot secondBallot = new(2, RA);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> m1Sealed2, _, ChangeOutcome<CheckpointCommitment> outcomeF2, bool sealedF2) =
            m1XRemoved.Seal(registerAfterF1, secondBallot, f2);
        Assert.IsTrue(sealedF2);

        //The drop-only seal keeps the base [a] and drops x — nothing converted.
        string[] baseOnly = ["a"];
        Assert.AreSequenceEqual(baseOnly, m1Sealed2.Values.ToArray());

        //The applier reaches the same state, then re-applies the SAME commitment: the drop-only projection at
        //F2 still matches the digest, so the re-apply succeeds idempotently.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> applied = m2XRemoved.ApplyCommittedSeal(outcomeF2.Value!, secondBallot);
        Assert.AreEqual(m1Sealed2.Live, applied.Live);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> reapplied = applied.ApplyCommittedSeal(outcomeF2.Value!, secondBallot);
        Assert.AreEqual(applied, reapplied);
    }


    private static CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> Sealable()
    {
        return CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Create(
            WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256);
    }


    /// <summary>
    /// The canonical bytes cover the dot AND the value deterministically: replica hex, counter, then value.
    /// </summary>
    private static ReadOnlyMemory<byte> Canonicalize(ImmutableArray<SequenceCheckpointEntry<string>> entries)
    {
        var builder = new StringBuilder();
        foreach(SequenceCheckpointEntry<string> entry in entries)
        {
            builder.Append(Convert.ToHexStringLower(entry.Dot.Replica.AsSpan()));
            builder.Append(':');
            builder.Append(entry.Dot.Counter);
            builder.Append(':');
            builder.Append(entry.Value);
            builder.Append('');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
