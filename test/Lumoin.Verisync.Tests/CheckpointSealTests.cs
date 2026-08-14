using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The seal protocol of <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/>: Seal is the sole
/// compaction entry point, driving a monotone dominate-or-equal register function through CASPaxos and, on
/// a won seal, compacting the live sequence against the frontier-keyed certified projection. Non-sealers
/// converge by applying the committed seal. Convergence is asserted COMPONENT-WISE — live sequence,
/// dotted checkpoint, and commitment — never through full-container equality, because
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> compares its checkpoint ballot too and
/// that legitimately differs between a sealer, an applier, and a higher-ballot re-seal.
/// </summary>
[TestClass]
internal sealed class CheckpointSealTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId RA { get; } = Replica(10);
    private static ReplicaId RB { get; } = Replica(20);
    private static ReplicaId RHigh { get; } = Replica(30);


    /// <summary>
    /// A won seal commits its commitment, compacts the live sequence at the frontier, and records the dotted
    /// certified projection.
    /// </summary>
    /// <remarks>
    /// T5: a,b inserted by R1, b removed; the projection at the full context is [a] and the dropped b
    /// translates to a.
    /// </remarks>
    [TestMethod]
    public void ASealCommitsCompactsAndRecordsTheDottedCheckpoint()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> afterSeal, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            removed.Seal(register, new Ballot(1, RA), frontier);

        Assert.IsTrue(wasSealed);
        Assert.IsTrue(outcome.IsChosen);

        //The checkpoint is the dotted certified projection at the frontier: b's remove is certified, so only
        //a survives, carrying its real vertex dot.
        ImmutableArray<SequenceCheckpointEntry<string>> expectedProjection = removed.Live.CertifiedProjection(frontier);
        Assert.AreSequenceEqual(expectedProjection.ToArray(), afterSeal.Checkpoint.ToArray());
        Assert.HasCount(1, afterSeal.Checkpoint);
        Assert.AreEqual("a", afterSeal.Checkpoint[0].Value);

        //The commitment is keyed to exactly the sealed frontier.
        Assert.AreEqual(frontier, afterSeal.Commitment!.Frontier);

        //The live sequence is compacted: b is dropped and served through the translation map onto a.
        string[] expectedValues = ["a"];
        Assert.AreSequenceEqual(expectedValues, afterSeal.Values.ToArray());
        Assert.AreEqual(idA, afterSeal.Live.TranslateAnchor(idB));
    }


    /// <summary>
    /// A non-sealer over the same full state applies the committed seal and converges component-wise — the
    /// determinism theorem end to end.
    /// </summary>
    [TestMethod]
    public void ANonSealerAppliesTheCommittedSealAndConverges()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<Rga<string>, string, Dot> sealer, _, ChangeOutcome<CheckpointCommitment> outcome, _) =
            removed.Seal(register, ballot, frontier);

        //A second container over the byte-identical full state applies the committed commitment.
        (CheckpointedSequence<Rga<string>, string, Dot> otherA, Dot otherIdA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> otherB, Dot otherIdB) = otherA.InsertAfter(otherIdA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> otherRemoved = otherB.Remove(otherIdB, R1);
        CheckpointedSequence<Rga<string>, string, Dot> applied = otherRemoved.ApplyCommittedSeal(outcome.Value!, ballot);

        Assert.AreEqual(sealer.Live, applied.Live);
        Assert.AreSequenceEqual(sealer.Checkpoint.ToArray(), applied.Checkpoint.ToArray());
        Assert.AreEqual(sealer.Commitment, applied.Commitment);
    }


    /// <summary>
    /// Re-applying an already-applied seal is idempotent for the identity-stable RGA strategy: its compaction
    /// preserves projection identities, so the applier's projection at the committed frontier still matches the
    /// digest on the second application and the container is unchanged.
    /// </summary>
    /// <remarks>
    /// The base-materializing offset analog instead fails closed once the seal converted live content — pinned
    /// in OffsetCheckpointSealTests.
    /// </remarks>
    [TestMethod]
    public void ReapplyingAnAppliedSealIsIdempotentForAnIdentityStableStrategy()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (_, _, ChangeOutcome<CheckpointCommitment> outcome, _) = removed.Seal(register, ballot, frontier);

        (CheckpointedSequence<Rga<string>, string, Dot> otherA, Dot otherIdA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> otherB, Dot otherIdB) = otherA.InsertAfter(otherIdA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> otherRemoved = otherB.Remove(otherIdB, R1);
        CheckpointedSequence<Rga<string>, string, Dot> applied = otherRemoved.ApplyCommittedSeal(outcome.Value!, ballot);

        CheckpointedSequence<Rga<string>, string, Dot> reapplied = applied.ApplyCommittedSeal(outcome.Value!, ballot);

        Assert.AreEqual(applied, reapplied);
    }


    /// <summary>
    /// A competing seal that is behind aborts unchanged, re-proposing the winner's commitment; a later seal
    /// that strictly dominates the winner succeeds — the committed chain only ascends.
    /// </summary>
    [TestMethod]
    public void ACompetingSealAbortsUnchangedAndSucceedsAboveTheWinner()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withC, _) = withB.InsertAfter(idB, "c", R1);

        VectorClock f0 = FrontierTo(1);
        VectorClock f1 = FrontierTo(2);
        VectorClock f2 = FrontierTo(3);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        //A seals at F1 and wins the first seal.
        (CheckpointedSequence<Rga<string>, string, Dot> sealedA, CasPaxosRegister<CheckpointCommitment> registerAfterA, _, bool aSealed) =
            withC.Seal(register, new Ballot(1, RA), f1);
        Assert.IsTrue(aSealed);

        //B seals at F0, which A's F1 strictly dominates, under a higher ballot: the refusal arm re-proposes
        //A's commitment, so B does not seal and its container is returned unchanged.
        (CheckpointedSequence<Rga<string>, string, Dot> sealedB, CasPaxosRegister<CheckpointCommitment> registerAfterB, ChangeOutcome<CheckpointCommitment> outcomeB, bool bSealed) =
            withC.Seal(registerAfterA, new Ballot(2, RB), f0);
        Assert.IsFalse(bSealed);
        Assert.AreEqual(withC, sealedB);
        Assert.AreEqual(sealedA.Commitment, outcomeB.Value);

        //Pinned component-wise too, so the abort contract holds even if the abort path ever stops
        //returning the same instance: nothing was recorded and nothing compacted.
        Assert.IsNull(sealedB.Commitment);
        Assert.IsNull(sealedB.CheckpointBallot);
        Assert.HasCount(0, sealedB.Checkpoint);
        Assert.AreEqual(withC.Live, sealedB.Live);

        //B then seals at F2, which strictly dominates F1: the chain ascends and the seal succeeds.
        (_, _, ChangeOutcome<CheckpointCommitment> outcomeB2, bool bSealed2) =
            withC.Seal(registerAfterB, new Ballot(3, RB), f2);
        Assert.IsTrue(bSealed2);
        Assert.IsTrue(outcomeB2.IsChosen);
    }


    /// <summary>
    /// Re-sealing the identical state at the identical frontier under a higher ballot hits the equal arm and
    /// seals again idempotently: live, checkpoint, and commitment equal the first seal's, only the recorded
    /// ballot advances.
    /// </summary>
    [TestMethod]
    public void AnEqualFrontierResealIsIdempotent()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = withA.InsertAfter(idA, "b", R1);

        VectorClock frontier = withB.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> first, CasPaxosRegister<CheckpointCommitment> registerAfterFirst, _, bool firstSealed) =
            withB.Seal(register, new Ballot(1, RA), frontier);
        Assert.IsTrue(firstSealed);

        (CheckpointedSequence<Rga<string>, string, Dot> second, _, _, bool secondSealed) =
            withB.Seal(registerAfterFirst, new Ballot(2, RA), frontier);

        Assert.IsTrue(secondSealed);
        Assert.AreEqual(first.Live, second.Live);
        Assert.AreSequenceEqual(first.Checkpoint.ToArray(), second.Checkpoint.ToArray());
        Assert.AreEqual(first.Commitment, second.Commitment);
        Assert.AreEqual(new Ballot(2, RA), second.CheckpointBallot);
    }


    /// <summary>
    /// Two states with byte-identical contexts but different values (a dishonest construction: R1 mints (R1,1)
    /// carrying "x" in one and "y" in the other) collide at the equal frontier with divergent digests; the
    /// second sealer is refused and its container is returned unchanged.
    /// </summary>
    [TestMethod]
    public void AnEqualFrontierDivergentDigestIsRefused()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> containerX, _) = Sealable().InsertAtHead("x", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> containerY, _) = Sealable().InsertAtHead("y", R1);

        VectorClock frontier = containerX.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (_, CasPaxosRegister<CheckpointCommitment> registerAfterX, _, bool xSealed) =
            containerX.Seal(register, new Ballot(1, RA), frontier);
        Assert.IsTrue(xSealed);

        (CheckpointedSequence<Rga<string>, string, Dot> sealedY, _, _, bool ySealed) =
            containerY.Seal(registerAfterX, new Ballot(2, RB), frontier);

        Assert.IsFalse(ySealed);
        Assert.AreEqual(containerY, sealedY);
        Assert.IsNull(sealedY.Commitment);
        Assert.IsNull(sealedY.CheckpointBallot);
        Assert.HasCount(0, sealedY.Checkpoint);
        Assert.AreEqual(containerY.Live, sealedY.Live);
    }


    /// <summary>
    /// Committed seals apply in chain order: a stale earlier commitment is rejected by frontier order even
    /// when its digest coincides with the applier's projection at that earlier frontier — the digest check
    /// alone cannot see the regression, so the chain-order guard must.
    /// </summary>
    [TestMethod]
    public void ApplyingAStaleEarlierSealFailsClosedByChainOrder()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> removed = withB.Remove(idB, R1);

        VectorClock f2 = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> afterSeal, _, _, bool wasSealed) =
            removed.Seal(register, new Ballot(1, RA), f2);
        Assert.IsTrue(wasSealed);

        //At F1 only a's insert is certified, so the sealed container's projection there is [a] — the same
        //content the stale commitment carries. Its digest coincides; only the chain-order guard can reject.
        VectorClock f1 = FrontierTo(1);
        var staleCommitted = new CheckpointCommitment(f1, Sha256(Canonicalize(afterSeal.Live.CertifiedProjection(f1))));

        Assert.ThrowsExactly<InvalidOperationException>(() => afterSeal.ApplyCommittedSeal(staleCommitted, new Ballot(2, RA)));
    }


    /// <summary>
    /// The rejoin recipe end to end: a rejoiner seeds a fresh container around a healthy donor's full
    /// sequence state with Adopt and applies the committed seal, whose digest verification is the adoption
    /// check; the result converges component-wise with the sealer.
    /// </summary>
    [TestMethod]
    public void ARejoinerAdoptsADonorStateAndAppliesTheCommittedSeal()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "b", R1);
        CheckpointedSequence<Rga<string>, string, Dot> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (CheckpointedSequence<Rga<string>, string, Dot> sealer, _, ChangeOutcome<CheckpointCommitment> outcome, _) =
            removed.Seal(register, ballot, frontier);

        //The rejoiner holds only the donor's transported full sequence state — no container history.
        CheckpointedSequence<Rga<string>, string, Dot> rejoiner = CheckpointedSequence<Rga<string>, string, Dot>.Adopt(
            WellKnownSequenceStrategies.CreateRgaRle<string>(), Canonicalize, Sha256, removed.Live);
        CheckpointedSequence<Rga<string>, string, Dot> applied = rejoiner.ApplyCommittedSeal(outcome.Value!, ballot);

        Assert.AreEqual(sealer.Live, applied.Live);
        Assert.AreSequenceEqual(sealer.Checkpoint.ToArray(), applied.Checkpoint.ToArray());
        Assert.AreEqual(sealer.Commitment, applied.Commitment);

        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Adopt(
            WellKnownSequenceStrategies.CreateRgaRle<string>(), Canonicalize, Sha256, null!));
    }


    /// <summary>
    /// Applying a committed seal whose digest disagrees with the applier's own certified projection at the
    /// committed frontier fails closed, even with the precondition satisfied — the "y" container applying the
    /// "x" commitment.
    /// </summary>
    [TestMethod]
    public void ApplyCommittedSealFailsClosedOnADivergentDigest()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> containerX, _) = Sealable().InsertAtHead("x", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> containerY, _) = Sealable().InsertAtHead("y", R1);

        VectorClock frontier = containerX.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        Ballot ballot = new(1, RA);
        (_, _, ChangeOutcome<CheckpointCommitment> outcomeX, _) = containerX.Seal(register, ballot, frontier);

        Assert.ThrowsExactly<InvalidOperationException>(() => containerY.ApplyCommittedSeal(outcomeX.Value!, ballot));
    }


    /// <summary>
    /// A strategy that certifies no projection cannot seal: the plain non-compacting RGA strategy throws.
    /// </summary>
    /// <remarks>
    /// offset.v2 satisfies the seal preconditions, pinned here by a minimal smoke seal — the offset seal
    /// end-to-end suite lives in OffsetCheckpointSealTests.
    /// </remarks>
    [TestMethod]
    public void SealRequiresACertifyingStrategy()
    {
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        CheckpointedSequence<Rga<string>, string, Dot> plainRga =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRga<string>(), Canonicalize, Sha256);
        Assert.ThrowsExactly<InvalidOperationException>(() => plainRga.Seal(register, new Ballot(1, RA), VectorClock.Empty));

        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> offset =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Create(
                WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withA, _) = offset.InsertAtHead("a", R1);
        CasPaxosRegister<CheckpointCommitment> offsetRegister = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> sealedOffset, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            withA.Seal(offsetRegister, new Ballot(1, RA), withA.CausalContext!);
        Assert.IsTrue(wasSealed);
        Assert.IsTrue(outcome.IsChosen);
        Assert.HasCount(1, sealedOffset.Checkpoint);
        Assert.AreEqual("a", sealedOffset.Checkpoint[0].Value);
    }


    /// <summary>
    /// Unstable edits above the frontier do not make Seal throw — the finding-3 false-throw shape is gone.
    /// </summary>
    /// <remarks>
    /// The checkpoint excludes the unstable edits, and a later seal covering them succeeds again.
    /// </remarks>
    [TestMethod]
    public void ASealWithUnstableEditsPresentSucceeds()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = withA.InsertAfter(idA, "b", R1);

        VectorClock f1 = FrontierTo(1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> first, CasPaxosRegister<CheckpointCommitment> registerAfterFirst, _, bool firstSealed) =
            withB.Seal(register, new Ballot(1, RA), f1);

        Assert.IsTrue(firstSealed);
        string[] excludesUnstable = ["a"];
        Assert.AreSequenceEqual(excludesUnstable, ProjectValues(first.Checkpoint));

        //A later seal at F2, which covers the previously unstable edit, seals again and captures it.
        VectorClock f2 = FrontierTo(2);
        (CheckpointedSequence<Rga<string>, string, Dot> second, _, _, bool secondSealed) =
            withB.Seal(registerAfterFirst, new Ballot(2, RA), f2);

        Assert.IsTrue(secondSealed);
        string[] coversBoth = ["a", "b"];
        Assert.AreSequenceEqual(coversBoth, ProjectValues(second.Checkpoint));
    }


    /// <summary>
    /// Seal and ApplyCommittedSeal reject null arguments, and a register that cannot reach a prepare quorum
    /// (pre-promised at a higher ballot) leaves the seal unchosen and the container unchanged.
    /// </summary>
    [TestMethod]
    public void SealAndApplyRejectNullsAndReportQuorumFailure()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = Sealable().InsertAtHead("a", R1);
        VectorClock frontier = withA.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        Assert.ThrowsExactly<ArgumentNullException>(() => withA.Seal(null!, new Ballot(1, RA), frontier));
        Assert.ThrowsExactly<ArgumentNullException>(() => withA.Seal(register, new Ballot(1, RA), null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => withA.ApplyCommittedSeal(null!, new Ballot(1, RA)));

        //A competing higher ballot promised on every acceptor makes the seal's lower-ballot prepare fail, so
        //no quorum is reached and the seal aborts unchanged.
        ReadOnlyMemory<byte> dummyDigest = new byte[] { 1 };
        (CasPaxosRegister<CheckpointCommitment> blocked, _) = register.Change(new Ballot(5, RHigh), _ => new CheckpointCommitment(VectorClock.Empty, dummyDigest));

        (CheckpointedSequence<Rga<string>, string, Dot> afterFailedSeal, _, ChangeOutcome<CheckpointCommitment> outcome, bool didSeal) =
            withA.Seal(blocked, new Ballot(1, RA), frontier);

        Assert.IsFalse(didSeal);
        Assert.IsFalse(outcome.IsChosen);
        Assert.AreEqual(withA, afterFailedSeal);
    }


    private static CheckpointedSequence<Rga<string>, string, Dot> Sealable()
    {
        return CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRgaRle<string>(), Canonicalize, Sha256);
    }


    /// <summary>
    /// A frontier that covers R1's axis up to the given counter, the honest "every member observed the first
    /// n R1 events" shape these single-replica seals use.
    /// </summary>
    private static VectorClock FrontierTo(int counter)
    {
        VectorClock frontier = VectorClock.Empty;
        while(frontier[R1] < counter)
        {
            frontier = frontier.Increment(R1);
        }

        return frontier;
    }


    private static string[] ProjectValues(ImmutableArray<SequenceCheckpointEntry<string>> checkpoint)
    {
        var values = new string[checkpoint.Length];
        for(int i = 0; i < checkpoint.Length; i++)
        {
            values[i] = checkpoint[i].Value;
        }

        return values;
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
            builder.Append('\u001F');
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
