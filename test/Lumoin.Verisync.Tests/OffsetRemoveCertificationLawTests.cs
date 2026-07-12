using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The slice-O1 remove-certification law suite for <see cref="OffsetAnchoredSequence{TValue}"/>: BOTH
/// removal kinds — live tombstones and base-offset removals — are dotted events on the shared counter
/// plane, the stability frontier certifies them, and certification alone gates the live drop, the
/// pending-removed conversion, and the projection; base slots are always kept, their reclamation
/// deferred to a consensus-carried follow-on. Frontiers
/// are folded from REAL gossip digests over each member's
/// <see cref="OffsetAnchoredSequence{TValue}.CausalContext"/> — including the laggard's — so the shipped
/// <see cref="StabilityFrontier"/> min-fold is exercised end to end. The certified projection at a
/// frontier includes a locally removed element whose remove is not yet certified — the determinism
/// inclusion that keeps two disagreeing members on one byte-identical base — and the base generation is
/// fenced by the consensus-stamped <c>BaseFrontier</c> identity, not by base value equality.
/// </summary>
[TestClass]
internal sealed class OffsetRemoveCertificationLawTests
{
    private static ReplicaId R1 { get; } = MakeReplica(1);
    private static ReplicaId R2 { get; } = MakeReplica(2);
    private static ReplicaId R3 { get; } = MakeReplica(3);

    //The replica axes the CsCheck op histories mint on; one replica per operand index.
    private static ReplicaId[] Replicas { get; } = [MakeReplica(10), MakeReplica(11), MakeReplica(12)];

    private static ImmutableArray<string> SingleBase { get; } = ["b0"];

    private static ImmutableArray<string> TripleBase { get; } = ["b0", "b1", "b2"];


    //T7 — THE load-bearing determinism regression: M1 observed a live remove and a base removal that M2
    //did not; both compact at a frontier certifying NEITHER. Ghost-retention or retain-on-uncertified
    //would fork the base value arrays; the pending-removed conversion keeps them byte-identical, the
    //generation identities equal, and the two states mergeable.
    [TestMethod]
    public void TwoMembersDisagreeingOnAnUncertifiedRemoveCompactToTheIdenticalBase()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(SingleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        OffsetAnchoredSequence<string> m2 = shared;
        OffsetAnchoredSequence<string> m1 = shared.Remove(x, R2).Remove(OffsetAnchor.AtBase(0), R2);

        //The frontier certifies x's insert but neither of M1's removes (the laggard M2 pins R2 at zero).
        VectorClock frontier = FrontierOf(m1.CausalContext, m2.CausalContext);

        //Both members derive the identical certified projection: the sentinel identity for the base slot,
        //the real vertex dot for the stable live element — the locally hidden elements stay IN.
        ImmutableArray<SequenceCheckpointEntry<string>> m1Projection = m1.CertifiedProjection(frontier);
        ImmutableArray<SequenceCheckpointEntry<string>> m2Projection = m2.CertifiedProjection(frontier);
        SequenceCheckpointEntry<string>[] expectedProjection =
        [
            new SequenceCheckpointEntry<string>(SentinelDot(0), "b0"),
            new SequenceCheckpointEntry<string>(DotStateOf(x.LiveId!), "x")
        ];
        CollectionAssert.AreEqual(expectedProjection, m1Projection.ToArray());
        CollectionAssert.AreEqual(expectedProjection, m2Projection.ToArray());

        OffsetAnchoredSequence<string> m1Compacted = m1.Compact(frontier, m1Projection);
        OffsetAnchoredSequence<string> m2Compacted = m2.Compact(frontier, m2Projection);

        //Byte-identical base value arrays: M1 converts x pending-removed, M2 converts it visible.
        string[] expectedBase = ["b0", "x"];
        CollectionAssert.AreEqual(expectedBase, m1Compacted.ToState().Base.ToArray());
        CollectionAssert.AreEqual(expectedBase, m2Compacted.ToState().Base.ToArray());

        //Identical generation identity: both compactions changed the base, so both stamp the frontier.
        Assert.AreEqual(frontier, VectorClock.FromState(m1Compacted.ToState().BaseFrontier));
        Assert.AreEqual(frontier, VectorClock.FromState(m2Compacted.ToState().BaseFrontier));

        //The two merge without throwing in either order; the per-offset union carries M1's markings.
        OffsetAnchoredSequence<string> forward = m1Compacted.Merge(m2Compacted);
        OffsetAnchoredSequence<string> backward = m2Compacted.Merge(m1Compacted);
        Assert.AreEqual(forward, backward);
        Assert.HasCount(0, forward.Values);
        Assert.HasCount(0, backward.Values);
    }


    //LAW-RG over the live axis: an UNCERTIFIED remove converts its stable target into the base as a
    //pending-removed entry (value carried, hidden locally, generation advanced); a CERTIFIED remove
    //drops the vertex without touching the base, so the generation identity does not advance.
    [TestMethod]
    public void RemoveObservationGatesTheDrop()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(SingleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        OffsetAnchoredSequence<string> withRemove = shared.Remove(x, R1);

        //F1: two members advertise the post-remove context, the laggard the pre-remove context, so the
        //min floors R1 below the remove-dot and the remove is not certified.
        VectorClock preRemove = shared.CausalContext;
        VectorClock postRemove = withRemove.CausalContext;
        VectorClock f1 = FrontierOf(postRemove, postRemove, preRemove);

        //The projection still carries the locally hidden x with its real dot.
        ImmutableArray<SequenceCheckpointEntry<string>> certifiedAtF1 = withRemove.CertifiedProjection(f1);
        SequenceCheckpointEntry<string>[] expectedAtF1 =
        [
            new SequenceCheckpointEntry<string>(SentinelDot(0), "b0"),
            new SequenceCheckpointEntry<string>(DotStateOf(x.LiveId!), "x")
        ];
        CollectionAssert.AreEqual(expectedAtF1, certifiedAtF1.ToArray());

        //Uncertified: x converts pending-removed — its value enters the new base, hidden locally.
        OffsetAnchoredSequence<string> pending = withRemove.Compact(f1, certifiedAtF1);
        string[] pendingBase = ["b0", "x"];
        string[] hiddenLocally = ["b0"];
        CollectionAssert.AreEqual(pendingBase, pending.ToState().Base.ToArray());
        CollectionAssert.AreEqual(hiddenLocally, pending.Values.ToArray());
        Assert.AreEqual(OffsetAnchor.AtBase(1), pending.TranslateAnchor(x));
        Assert.AreEqual(f1, VectorClock.FromState(pending.ToState().BaseFrontier));

        //The laggard observes the remove; the drop fires and the base is untouched, so the generation
        //identity carries unchanged.
        VectorClock f2 = FrontierOf(postRemove, postRemove, postRemove);
        ImmutableArray<SequenceCheckpointEntry<string>> certifiedAtF2 = withRemove.CertifiedProjection(f2);
        SequenceCheckpointEntry<string>[] expectedAtF2 = [new SequenceCheckpointEntry<string>(SentinelDot(0), "b0")];
        CollectionAssert.AreEqual(expectedAtF2, certifiedAtF2.ToArray());

        OffsetAnchoredSequence<string> dropped = withRemove.Compact(f2, certifiedAtF2);
        CollectionAssert.AreEqual(hiddenLocally, dropped.Values.ToArray());
        CollectionAssert.AreEqual(SingleBase.ToArray(), dropped.ToState().Base.ToArray());
        Assert.AreEqual(OffsetAnchor.AtBase(0), dropped.TranslateAnchor(x));
        Assert.AreEqual(VectorClock.Empty, VectorClock.FromState(dropped.ToState().BaseFrontier));
    }


    //LAW-NR: merging a former laggard that holds the vertex AND its dotted tombstone re-enters the ghost
    //hidden, never resurrecting the committed remove, and recompacting either merge order returns the
    //compacted state.
    [TestMethod]
    public void MergeDoesNotResurrectACommittedRemove()
    {
        RemoveScenario scenario = BuildRemoveScenario();
        OffsetAnchoredSequence<string> x = scenario.Compacted;
        OffsetAnchoredSequence<string> y = scenario.GhostHolder;

        string[] visible = ["b0"];
        CollectionAssert.AreEqual(visible, x.Merge(y).Values.ToArray());
        CollectionAssert.AreEqual(visible, y.Merge(x).Values.ToArray());

        Assert.AreEqual(x, x.Merge(y).Compact(scenario.Frontier, scenario.Checkpoint));
        Assert.AreEqual(x, y.Merge(x).Compact(scenario.Frontier, scenario.Checkpoint));
    }


    //LAW-SR: a stale pre-remove state (element live, no tombstone) fails the stale-replay detector closed
    //in BOTH merge orders, while the honest ghost-holder throws in neither. A tombstone-only drop leaves
    //the base and its generation identity unchanged, so the fence passes and the detector is the fence.
    [TestMethod]
    public void AStalePreRemoveStateFailsClosedInBothMergeOrders()
    {
        RemoveScenario scenario = BuildRemoveScenario();
        OffsetAnchoredSequence<string> x = scenario.Compacted;
        OffsetAnchoredSequence<string> z = scenario.StalePreRemove;
        OffsetAnchoredSequence<string> y = scenario.GhostHolder;

        Assert.ThrowsExactly<InvalidOperationException>(() => x.Merge(z));
        Assert.ThrowsExactly<InvalidOperationException>(() => z.Merge(x));

        //The honest ghost-holder is not stale: it re-enters the ghost with its tombstone and never throws.
        string[] visible = ["b0"];
        CollectionAssert.AreEqual(visible, x.Merge(y).Values.ToArray());
        CollectionAssert.AreEqual(visible, y.Merge(x).Values.ToArray());
    }


    //LAW-NFD: merging an empty or a strictly-behind operand never drops a live element — the merge equals
    //the full state's visible values in every order. Histories run over the empty base so the empty
    //sequence shares the operands' generation.
    [TestMethod]
    public void MergeWithEmptyOrABehindOperandNeverDropsALiveElement()
    {
        GenHistoryWithCut.Sample(input =>
        {
            int[] full = input.Full.Values.ToArray();
            CollectionAssert.AreEqual(full, input.Full.Merge(OffsetAnchoredSequence<int>.Empty).Values.ToArray());
            CollectionAssert.AreEqual(full, OffsetAnchoredSequence<int>.Empty.Merge(input.Full).Values.ToArray());
            CollectionAssert.AreEqual(full, input.Full.Merge(input.Prefix).Values.ToArray());
            CollectionAssert.AreEqual(full, input.Prefix.Merge(input.Full).Values.ToArray());
        });
    }


    //Base-axis LAW-RG under deferred reclamation: an uncertified base removal keeps the slot in the
    //certified projection — the determinism inclusion on the base axis — while a certified one drops it
    //from the projection. In BOTH cases the slot survives compaction as the hidden ordering placeholder
    //with its marking riding to the shifted offset: observation gates the certification, and reclamation
    //waits for a consensus-carried follow-on.
    [TestMethod]
    public void ABaseRemovalObservationGatesTheCertification()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(TripleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R2);
        OffsetAnchoredSequence<string> removed = shared.Remove(OffsetAnchor.AtBase(1), R1);

        //The laggard never saw the removal, so the frontier certifies x's insert but not the removal and
        //the removed slot stays in the projection.
        VectorClock uncertified = FrontierOf(removed.CausalContext, shared.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> projectionUncertified = removed.CertifiedProjection(uncertified);
        SequenceCheckpointEntry<string>[] expectedUncertified =
        [
            new SequenceCheckpointEntry<string>(SentinelDot(0), "b0"),
            new SequenceCheckpointEntry<string>(DotStateOf(x.LiveId!), "x"),
            new SequenceCheckpointEntry<string>(SentinelDot(1), "b1"),
            new SequenceCheckpointEntry<string>(SentinelDot(2), "b2")
        ];
        CollectionAssert.AreEqual(expectedUncertified, projectionUncertified.ToArray());

        //x converts into the base ahead of the removed slot, so the kept slot shifts and its marking
        //rides along to the new offset.
        OffsetAnchoredSequence<string> kept = removed.Compact(uncertified, projectionUncertified);
        string[] keptBase = ["b0", "x", "b1", "b2"];
        string[] visible = ["b0", "x", "b2"];
        CollectionAssert.AreEqual(keptBase, kept.ToState().Base.ToArray());
        CollectionAssert.AreEqual(visible, kept.Values.ToArray());
        Assert.AreEqual(OffsetAnchor.AtBase(2), kept.TranslateAnchor(OffsetAnchor.AtBase(1)));
        OffsetBaseRemovalEntry riddenMarking = kept.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(2, riddenMarking.Offset);
        Assert.HasCount(1, riddenMarking.RemoveDots);

        //Every member observed the removal: the slot leaves the projection, yet compaction still keeps it
        //hidden at its shifted offset — certified means RECLAIMABLE by a follow-on, not reclaimed here.
        VectorClock certified = FrontierOf(removed.CausalContext, removed.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> projectionCertified = removed.CertifiedProjection(certified);
        SequenceCheckpointEntry<string>[] expectedCertified =
        [
            new SequenceCheckpointEntry<string>(SentinelDot(0), "b0"),
            new SequenceCheckpointEntry<string>(DotStateOf(x.LiveId!), "x"),
            new SequenceCheckpointEntry<string>(SentinelDot(2), "b2")
        ];
        CollectionAssert.AreEqual(expectedCertified, projectionCertified.ToArray());

        OffsetAnchoredSequence<string> stillKept = removed.Compact(certified, projectionCertified);
        CollectionAssert.AreEqual(keptBase, stillKept.ToState().Base.ToArray());
        CollectionAssert.AreEqual(visible, stillKept.Values.ToArray());
        Assert.AreEqual(OffsetAnchor.AtBase(2), stillKept.TranslateAnchor(OffsetAnchor.AtBase(1)));
        OffsetBaseRemovalEntry certifiedMarking = stillKept.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(2, certifiedMarking.Offset);
        Assert.HasCount(1, certifiedMarking.RemoveDots);
        Assert.AreEqual(certified, VectorClock.FromState(stillKept.ToState().BaseFrontier));
    }


    //NR-base under deferred reclamation, now under the §17 insert-quiescence contract: a certified base
    //removal rides its marking through a base-changing compaction and stays hidden, two same-generation
    //members that disagree on an uncertified LIVE remove still converge to the identical base, and a
    //previous-generation operand is fenced rather than resurrecting the value. The divergent-child
    //re-anchor §17 identified is foreclosed at the source — compacting a member that carries an
    //above-frontier child fails closed — so cross-member agreement is reached by compacting quiescent
    //states, with divergence confined to removes.
    [TestMethod]
    public void ACertifiedBaseRemovalStaysHiddenAcrossGenerationsAndMerges()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(TripleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        OffsetAnchoredSequence<string> removed = shared.Remove(OffsetAnchor.AtBase(1), R1);

        //The frontier certifies x's insert and the base removal, so the state is insert-quiescent.
        VectorClock frontier = FrontierOf(removed.CausalContext, removed.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = removed.CertifiedProjection(frontier);

        //A base-changing compaction: x converts ahead of the removed slot, the certified-removed slot is
        //KEPT hidden at its shifted offset with its marking riding forward, and the generation advances.
        OffsetAnchoredSequence<string> gen1 = removed.Compact(frontier, checkpoint);
        string[] gen1Base = ["b0", "x", "b1", "b2"];
        string[] gen1Visible = ["b0", "x", "b2"];
        CollectionAssert.AreEqual(gen1Base, gen1.ToState().Base.ToArray());
        CollectionAssert.AreEqual(gen1Visible, gen1.Values.ToArray());
        Assert.AreEqual(frontier, VectorClock.FromState(gen1.ToState().BaseFrontier));
        OffsetBaseRemovalEntry carried = gen1.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(2, carried.Offset);
        Assert.HasCount(1, carried.RemoveDots);

        //A same-generation peer that hid x with an uncertified live remove converts it pending-removed to
        //the identical base, so the two merge in both orders and the hidden set is the union.
        OffsetAnchoredSequence<string> peer = removed.Remove(x, R3).Compact(frontier, checkpoint);
        OffsetAnchoredSequence<string> forward = gen1.Merge(peer);
        OffsetAnchoredSequence<string> backward = peer.Merge(gen1);
        string[] merged = ["b0", "b2"];
        CollectionAssert.AreEqual(merged, forward.Values.ToArray());
        CollectionAssert.AreEqual(merged, backward.Values.ToArray());
        Assert.AreEqual(forward, backward);

        //A previous-generation operand cannot re-enter to resurrect the value: the fence throws, both orders.
        Assert.ThrowsExactly<InvalidOperationException>(() => gen1.Merge(removed));
        Assert.ThrowsExactly<InvalidOperationException>(() => removed.Merge(gen1));

        //Adding an above-frontier child makes the state non-quiescent, so its compaction fails closed —
        //the divergent-child re-anchor §17 forecloses.
        (OffsetAnchoredSequence<string> withUnstableChild, _) = removed.InsertAfter(OffsetAnchor.AtBase(2), "u1", R2);
        Assert.ThrowsExactly<InvalidOperationException>(() => withUnstableChild.Compact(frontier, withUnstableChild.CertifiedProjection(frontier)));
    }


    //Concurrent base removals of the same offset union per offset in both merge orders, and certification
    //needs only ONE of the unioned remove-dots below the frontier: the slot leaves the certified
    //projection, while compaction keeps it as the hidden placeholder — reclamation is deferred.
    [TestMethod]
    public void AConcurrentBaseRemovalConverges()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(TripleBase);
        OffsetAnchoredSequence<string> byFirst = shared.Remove(OffsetAnchor.AtBase(1), R1);
        OffsetAnchoredSequence<string> bySecond = shared.Remove(OffsetAnchor.AtBase(1), R2);

        OffsetAnchoredSequence<string> forward = byFirst.Merge(bySecond);
        OffsetAnchoredSequence<string> backward = bySecond.Merge(byFirst);
        Assert.AreEqual(forward, backward);

        //The per-offset union carries both remove-dots.
        OffsetBaseRemovalEntry entry = forward.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, entry.Offset);
        Assert.HasCount(2, entry.RemoveDots);

        //A frontier certifying only R1's remove-dot certifies the removal in either merge order: the
        //slot leaves the projection.
        VectorClock frontier = FrontierOf(forward.CausalContext, byFirst.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> forwardProjection = forward.CertifiedProjection(frontier);
        SequenceCheckpointEntry<string>[] expectedProjection =
        [
            new SequenceCheckpointEntry<string>(SentinelDot(0), "b0"),
            new SequenceCheckpointEntry<string>(SentinelDot(2), "b2")
        ];
        CollectionAssert.AreEqual(expectedProjection, forwardProjection.ToArray());
        CollectionAssert.AreEqual(expectedProjection, backward.CertifiedProjection(frontier).ToArray());

        //Compaction keeps the certified-removed slot as the hidden ordering placeholder in either order.
        OffsetAnchoredSequence<string> forwardCompacted = forward.Compact(frontier, forwardProjection);
        OffsetAnchoredSequence<string> backwardCompacted = backward.Compact(frontier, backward.CertifiedProjection(frontier));
        string[] visible = ["b0", "b2"];
        CollectionAssert.AreEqual(TripleBase.ToArray(), forwardCompacted.ToState().Base.ToArray());
        CollectionAssert.AreEqual(visible, forwardCompacted.Values.ToArray());
        Assert.AreEqual(forwardCompacted, backwardCompacted);
    }


    //T9 re-pointed to the deferred-reclamation posture: value cycles are unconstructible without
    //reclamation, so the generation fence is pinned directly. Two hand-built states carry EQUAL base
    //value arrays but DIFFERENT generation identities — the shape a reclaim-then-reconstruct cycle would
    //produce — and Merge throws in both orders, which bare BaseEqual would pass. The stamping rule the
    //fence rests on is pinned alongside: a converting compaction stamps the identity, a drop-only one
    //carries it unchanged.
    //
    //These sub-cases do NOT discriminate trap-2 — a base-changed flag derived from
    //!BaseEqual(newBase, Base) rather than from the conversion branches. Under deferred reclamation the
    //two derivations agree on every reachable input, because a value-cycling generation ([a]->..->[a])
    //is unconstructible without reclamation, so this suite CANNOT tell them apart. Trap-1 (a
    //conversion-counter flag that omits the pending-removed branch) is covered by T7. The white-box
    //regression that WOULD discriminate trap-2 — a compaction whose newBase value-array equals the
    //pre-compaction base yet must still stamp a new BaseFrontier — is a REQUIRED gate of the reclamation
    //follow-on, where value cycles first become constructible and the !BaseEqual derivation would
    //silently break the NR-base fence.
    [TestMethod]
    public void AnEqualValueBaseCannotCrossGenerations()
    {
        ImmutableArray<string> equalBase = ["a"];
        OffsetAnchoredSequenceState<string> genesis = OffsetAnchoredSequence<string>.WithBase(equalBase).ToState();
        VectorClockState laterIdentity = VectorClock.Empty.Increment(R1).ToState();
        OffsetAnchoredSequence<string> stale = OffsetAnchoredSequence<string>.FromState(genesis);
        OffsetAnchoredSequence<string> cycled = OffsetAnchoredSequence<string>.FromState(genesis with
        {
            BaseFrontier = laterIdentity,
            Context = laterIdentity
        });

        //The value arrays are EQUAL; only the generation identity discriminates them.
        CollectionAssert.AreEqual(stale.ToState().Base.ToArray(), cycled.ToState().Base.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => stale.Merge(cycled));
        Assert.ThrowsExactly<InvalidOperationException>(() => cycled.Merge(stale));

        //A converting compaction changed the base, so it stamps the frontier as the new identity.
        (OffsetAnchoredSequence<string> withInsert, _) = OffsetAnchoredSequence<string>.WithBase(equalBase).InsertAtHead("h", R1);
        VectorClock convertFrontier = FrontierOf(withInsert.CausalContext, withInsert.CausalContext);
        OffsetAnchoredSequence<string> converted = withInsert.Compact(convertFrontier, withInsert.CertifiedProjection(convertFrontier));
        Assert.AreEqual(convertFrontier, VectorClock.FromState(converted.ToState().BaseFrontier));

        //A drop-only compaction leaves the base untouched and carries the genesis identity unchanged.
        (OffsetAnchoredSequence<string> withGhost, OffsetAnchor g) = OffsetAnchoredSequence<string>.WithBase(equalBase).InsertAtHead("g", R1);
        OffsetAnchoredSequence<string> ghostRemoved = withGhost.Remove(g, R1);
        VectorClock dropFrontier = FrontierOf(ghostRemoved.CausalContext, ghostRemoved.CausalContext);
        OffsetAnchoredSequence<string> dropped = ghostRemoved.Compact(dropFrontier, ghostRemoved.CertifiedProjection(dropFrontier));
        CollectionAssert.AreEqual(equalBase.ToArray(), dropped.ToState().Base.ToArray());
        Assert.AreEqual(OffsetAnchor.Head, dropped.TranslateAnchor(g));
        Assert.AreEqual(VectorClock.Empty, VectorClock.FromState(dropped.ToState().BaseFrontier));
    }


    //RE-POINTED under §17: the pre-§17 A-killer compacted a laggard carrying above-frontier inserts (c
    //under the tombstoned x, g after d) to check the ghost held c UNDER x. The insert-quiescence guard
    //forecloses that whole shape — a non-quiescent compaction fails closed rather than reorder the
    //visible sequence into the ghost region — so only the quiescent peer compaction is admitted, and the
    //laggard and the merged state both throw.
    [TestMethod]
    public void ACompactionCarryingAnAboveFrontierInsertAfterATombstonedElementFailsClosed()
    {
        (OffsetAnchoredSequence<int> withA, OffsetAnchor a) = OffsetAnchoredSequence<int>.Empty.InsertAtHead(1, R1);
        (OffsetAnchoredSequence<int> withX, OffsetAnchor x) = withA.InsertAfter(a, 2, R1);
        (OffsetAnchoredSequence<int> withD, OffsetAnchor d) = withX.InsertAfter(a, 3, R1);
        OffsetAnchoredSequence<int> observed = withD.Remove(x, R1);

        //R2 branched before the remove and inserted g=(R2,4) after d; R3 observed the remove and inserted
        //c=(R3,5) after the tombstoned x. The laggard operand holds both above-frontier inserts.
        (OffsetAnchoredSequence<int> r2State, _) = withD.InsertAfter(d, 7, R2);
        (OffsetAnchoredSequence<int> r3State, _) = observed.InsertAfter(x, 4, R3);
        OffsetAnchoredSequence<int> laggard = r3State.Merge(r2State);

        //The frontier certifies a, x, d, and x's remove; c and g stay above it as unstable vertices.
        VectorClock frontier = FrontierOf(observed.CausalContext, laggard.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = observed.CertifiedProjection(frontier);

        //The peer is insert-quiescent — a, x, d all stable — so its compaction is admitted and preserves
        //the visible order: x drops childless, a and d convert in place.
        OffsetAnchoredSequence<int> peer = observed.Compact(frontier, checkpoint);
        int[] peerVisible = [1, 3];
        CollectionAssert.AreEqual(peerVisible, peer.Values.ToArray());

        //The laggard and the merged state each carry an unstable vertex, so the base-materializing
        //compaction fails closed — the guard forecloses the ghost-region reorder the old test measured.
        Assert.ThrowsExactly<InvalidOperationException>(() => laggard.Compact(frontier, checkpoint));
        Assert.ThrowsExactly<InvalidOperationException>(() => observed.Merge(laggard).Compact(frontier, checkpoint));
    }


    //§17 REGRESSION (skeptic: deferred-reclamation, the ghost-chain reorder). U hangs off the ghost chain
    //P->G while its stable sibling V converts. Pre-§17, base slots linearize after the Head region, so V
    //(a converted base slot) jumped PAST U (still under the ghost G), silently reordering committed live
    //content. The insert-quiescence guard forecloses it: U is an unstable vertex, so the compaction fails
    //closed rather than materialize a line the ghost chain would reorder.
    [TestMethod]
    public void AGhostChainReorderFailsClosedUnderTheQuiescenceGuard()
    {
        (OffsetAnchoredSequence<string> seq, VectorClock laggard) = BuildGhostChainShape();

        //F certifies the P and G removes but floors U (on R3) below the waterline — the exact frontier
        //that pre-§17 held U under the ghost chain while V converted out from under it.
        VectorClock frontier = FrontierOf(seq.CausalContext, laggard);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = seq.CertifiedProjection(frontier);
        Assert.ThrowsExactly<InvalidOperationException>(() => seq.Compact(frontier, checkpoint));
    }


    //§17 REGRESSION (skeptic: certification/taxonomy, the certified-ghost sibling-conversion reorder). P
    //is certified-removed and ghost-retained because its concurrent child C2 is unstable, while its stable
    //child C1 converts into a base slot. Pre-§17, C1 (canonically ahead of C2) was torn out of the ghost's
    //Head subtree into the base region, which linearizes AFTER it, so C1 landed behind C2 and the visible
    //order changed. The guard forecloses it: C2 is an unstable vertex, so the compaction fails closed.
    [TestMethod]
    public void ACertifiedGhostSiblingConversionReorderFailsClosedUnderTheQuiescenceGuard()
    {
        (OffsetAnchoredSequence<string> root, OffsetAnchor p) = OffsetAnchoredSequence<string>.Empty.InsertAtHead("P", R1);
        (OffsetAnchoredSequence<string> earlyBranch, _) = root.InsertAfter(p, "C2", R3);
        OffsetAnchoredSequence<string> shared = root.Remove(p, R1);
        (shared, _) = shared.InsertAfter(p, "C1", R2);
        OffsetAnchoredSequence<string> full = shared.Merge(earlyBranch);

        //The laggard `shared` never observed C2, so the min-fold floors R3 below C2: P's remove is
        //certified, C1 is stable, and C2 is unstable — the reorder shape.
        VectorClock frontier = FrontierOf(full.CausalContext, shared.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = full.CertifiedProjection(frontier);
        Assert.ThrowsExactly<InvalidOperationException>(() => full.Compact(frontier, checkpoint));
    }


    //§17 REGRESSION (skeptic: deferred-reclamation, the frontier-path wedge). Pre-§17 a member could
    //compact along a path that left U unstable (F) and reach a base ordered differently from a member that
    //compacted once U was stable (F'), yet both stamped the SAME generation identity — honest members
    //permanently unmergeable on the BaseEqual assertion, their same-frontier projections divergent. The
    //guard admits only the quiescent path: the non-quiescent frontier throws, and the quiescent one
    //preserves the visible order.
    [TestMethod]
    public void AFrontierPathWedgeFailsClosedUnderTheQuiescenceGuard()
    {
        (OffsetAnchoredSequence<string> seq, VectorClock laggard) = BuildGhostChainShape();

        //The non-quiescent frontier path (F leaves U unstable) is the one that produced the wedge; it now
        //fails closed.
        VectorClock nonQuiescent = FrontierOf(seq.CausalContext, laggard);
        Assert.ThrowsExactly<InvalidOperationException>(() => seq.Compact(nonQuiescent, seq.CertifiedProjection(nonQuiescent)));

        //Only the quiescent frontier (F' certifies U) is admitted, and it preserves the visible order.
        VectorClock quiescent = FrontierOf(seq.CausalContext, seq.CausalContext);
        OffsetAnchoredSequence<string> compacted = seq.Compact(quiescent, seq.CertifiedProjection(quiescent));
        string[] order = ["V", "U"];
        CollectionAssert.AreEqual(order, compacted.Values.ToArray());
    }


    //G4: a 10,000-deep tombstoned chain must classify, walk, and enumerate iteratively without stack
    //growth. Every remove is certified at the state's own context, so the whole chain drops and every
    //dropped dot translates to the base slot it hung under.
    [TestMethod]
    public void ADeepTombstoneRunCompactsWithoutRecursion()
    {
        const int Depth = 10_000;
        ImmutableArray<int> deepBase = [0];
        ImmutableArray<byte> r1Bytes = ImmutableArray.Create(R1.AsSpan());
        ImmutableArray<byte> r2Bytes = ImmutableArray.Create(R2.AsSpan());

        //Built in one FromState pass to avoid quadratic immutable rebuilds: a chain (R1,1..Depth) hangs
        //under base slot 0, each next element anchored at the previous, every element tombstoned by a
        //dotted remove on R2's axis.
        ImmutableArray<OffsetVertexEntry<int>>.Builder vertices = ImmutableArray.CreateBuilder<OffsetVertexEntry<int>>(Depth);
        ImmutableArray<OffsetTombstoneEntry>.Builder tombstones = ImmutableArray.CreateBuilder<OffsetTombstoneEntry>(Depth);
        for(int i = 1; i <= Depth; i++)
        {
            OffsetAnchorState anchor = i == 1
                ? new OffsetAnchorState(0, null)
                : new OffsetAnchorState(-1, new DotState(r1Bytes, i - 1));
            vertices.Add(new OffsetVertexEntry<int>(new DotState(r1Bytes, i), anchor, i));
            tombstones.Add(new OffsetTombstoneEntry(new DotState(r1Bytes, i), [new DotState(r2Bytes, i)]));
        }

        VectorClockState context = new([new ReplicaCounterEntry(r1Bytes, Depth), new ReplicaCounterEntry(r2Bytes, Depth)]);
        OffsetAnchoredSequenceState<int> state = OffsetAnchoredSequence<int>.WithBase(deepBase).ToState() with
        {
            Vertices = vertices.ToImmutable(),
            Tombstones = tombstones.ToImmutable(),
            Context = context
        };
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.FromState(state);

        //Enumerating the visible walk crosses the hidden chain — the ordering walk must be iterative too.
        int[] expected = [0];
        CollectionAssert.AreEqual(expected, sequence.Values.ToArray());

        VectorClock frontier = FrontierOf(sequence.CausalContext, sequence.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<int> compacted = sequence.Compact(frontier, checkpoint);
        CollectionAssert.AreEqual(expected, compacted.Values.ToArray());

        bool everyDroppedDotTranslatesToTheSlot = true;
        for(int i = 1; i <= Depth; i++)
        {
            if(!OffsetAnchor.AtBase(0).Equals(compacted.TranslateAnchor(OffsetAnchor.AtLive(new Dot(R1, i)))))
            {
                everyDroppedDotTranslatesToTheSlot = false;

                break;
            }
        }

        Assert.IsTrue(everyDroppedDotTranslatesToTheSlot);
    }


    //T8 — the pending-removed lifecycle under deferred reclamation: an uncertified-removed stable vertex
    //converts into the base as a pending-removed entry carrying its remove-dot; when a later frontier
    //certifies the remove, the slot leaves the certified projection — RECLAIMABLE by a consensus-carried
    //follow-on — yet the marking persists and the slot stays hidden in place.
    [TestMethod]
    public void PendingRemovedConversionCarriesTheRemoveAcrossTheGeneration()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(SingleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        OffsetAnchoredSequence<string> removed = shared.Remove(x, R2);

        //The frontier certifies x's insert but not R2's remove.
        VectorClock frontier = FrontierOf(removed.CausalContext, shared.CausalContext);
        OffsetAnchoredSequence<string> generationOne = removed.Compact(frontier, removed.CertifiedProjection(frontier));

        //The conversion carried the value into the base, hidden, with the remove-dot keyed to the slot.
        string[] pendingBase = ["b0", "x"];
        string[] hidden = ["b0"];
        CollectionAssert.AreEqual(pendingBase, generationOne.ToState().Base.ToArray());
        CollectionAssert.AreEqual(hidden, generationOne.Values.ToArray());
        Assert.AreEqual(frontier, VectorClock.FromState(generationOne.ToState().BaseFrontier));
        OffsetBaseRemovalEntry marking = generationOne.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, marking.Offset);
        Assert.HasCount(1, marking.RemoveDots);
        Assert.AreEqual(1, marking.RemoveDots[0].Counter);
        Assert.AreEqual(2, marking.RemoveDots[0].Replica[0]);

        //Every member observes the remove: the slot leaves the projection at the certifying frontier.
        VectorClock certified = FrontierOf(removed.CausalContext, removed.CausalContext);
        ImmutableArray<SequenceCheckpointEntry<string>> certifiedProjection = generationOne.CertifiedProjection(certified);
        SequenceCheckpointEntry<string>[] expectedCertified = [new SequenceCheckpointEntry<string>(SentinelDot(0), "b0")];
        CollectionAssert.AreEqual(expectedCertified, certifiedProjection.ToArray());

        //Compaction at the certifying frontier keeps the slot: the base, the marking, and the hidden
        //ordering placeholder all persist, and the converted dot keeps translating to the kept slot.
        OffsetAnchoredSequence<string> laterGeneration = generationOne.Compact(certified, certifiedProjection);
        Assert.AreEqual(generationOne, laterGeneration);
        CollectionAssert.AreEqual(pendingBase, laterGeneration.ToState().Base.ToArray());
        CollectionAssert.AreEqual(hidden, laterGeneration.Values.ToArray());
        OffsetBaseRemovalEntry persisted = laterGeneration.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, persisted.Offset);
        Assert.HasCount(1, persisted.RemoveDots);
        Assert.AreEqual(OffsetAnchor.AtBase(1), laterGeneration.TranslateAnchor(x));
    }


    //RE-POINTED under §17: Invariant RA guarded the re-anchoring of survivors across divergent unstable
    //child sets. That re-anchoring is foreclosed at the source — a state with an above-frontier child is
    //non-quiescent and Compact fails closed, so no survivor re-anchors and there is nothing to disagree
    //on. The certified projection stays an UNRESTRICTED pure read, so two members with different unstable
    //child sets still agree on it at the shared frontier; only the base-materializing compaction is gated.
    [TestMethod]
    public void DivergentUnstableChildrenAgreeOnTheProjectionButFailClosedOnCompaction()
    {
        OffsetAnchoredSequence<string> prefix = OffsetAnchoredSequence<string>.WithBase(SingleBase);
        (prefix, OffsetAnchor p) = prefix.InsertAfter(OffsetAnchor.AtBase(0), "p", R1);
        (prefix, OffsetAnchor q) = prefix.InsertAfter(p, "q", R1);
        prefix = prefix.Remove(q, R2);
        VectorClock prefixContext = prefix.CausalContext;

        //The shared survivor s is above the frontier; each member then adds its own unstable child.
        (OffsetAnchoredSequence<string> shared, _) = prefix.InsertAfter(p, "s", R1);
        (OffsetAnchoredSequence<string> m1, _) = shared.InsertAfter(p, "u1", R2);
        (OffsetAnchoredSequence<string> m2, _) = shared.InsertAfter(OffsetAnchor.AtBase(0), "u2", R3);

        VectorClock frontier = FrontierOf(m1.CausalContext, m2.CausalContext, prefixContext);
        ImmutableArray<SequenceCheckpointEntry<string>> m1Projection = m1.CertifiedProjection(frontier);
        ImmutableArray<SequenceCheckpointEntry<string>> m2Projection = m2.CertifiedProjection(frontier);

        //The unrestricted projection agrees across the two members despite their divergent unstable children.
        CollectionAssert.AreEqual(m1Projection.ToArray(), m2Projection.ToArray());

        //But s, u1, and u2 all sit above the frontier, so the base-materializing compaction fails closed
        //on either member — the guard forecloses the re-anchor determinism hazard entirely.
        Assert.ThrowsExactly<InvalidOperationException>(() => m1.Compact(frontier, m1Projection));
        Assert.ThrowsExactly<InvalidOperationException>(() => m2.Compact(frontier, m2Projection));
    }


    private readonly record struct RemoveScenario(
        OffsetAnchoredSequence<string> Compacted,
        OffsetAnchoredSequence<string> GhostHolder,
        OffsetAnchoredSequence<string> StalePreRemove,
        VectorClock Frontier,
        ImmutableArray<SequenceCheckpointEntry<string>> Checkpoint);


    private static RemoveScenario BuildRemoveScenario()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(SingleBase);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        OffsetAnchoredSequence<string> stalePreRemove = shared;
        OffsetAnchoredSequence<string> ghostHolder = shared.Remove(x, R1);

        VectorClock postRemove = ghostHolder.CausalContext;
        VectorClock frontier = FrontierOf(postRemove, postRemove, postRemove);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = ghostHolder.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = ghostHolder.Compact(frontier, checkpoint);

        return new RemoveScenario(compacted, ghostHolder, stalePreRemove, frontier, checkpoint);
    }


    //The ghost-chain shape shared by the §17 reorder and wedge regressions: P at head, G and V after P, U
    //after the ghost G, then P and G removed — so U hangs off the ghost chain P->G while V is P's stable
    //sibling. A laggard that never observed U pins its axis at zero, so a min-fold frontier certifies the
    //P and G removes yet floors U below the waterline. Returns the sequence and the laggard's context.
    private static (OffsetAnchoredSequence<string> Sequence, VectorClock Laggard) BuildGhostChainShape()
    {
        (OffsetAnchoredSequence<string> seq, OffsetAnchor p) = OffsetAnchoredSequence<string>.Empty.InsertAtHead("P", R1);
        (seq, OffsetAnchor g) = seq.InsertAfter(p, "G", R2);
        (seq, _) = seq.InsertAfter(p, "V", R2);
        (seq, _) = seq.InsertAfter(g, "U", R3);
        seq = seq.Remove(p, R1).Remove(g, R2);

        (OffsetAnchoredSequence<string> laggard, OffsetAnchor laggardP) = OffsetAnchoredSequence<string>.Empty.InsertAtHead("P", R1);
        (laggard, OffsetAnchor laggardG) = laggard.InsertAfter(laggardP, "G", R2);
        (laggard, _) = laggard.InsertAfter(laggardP, "V", R2);
        laggard = laggard.Remove(laggardP, R1).Remove(laggardG, R2);

        return (seq, laggard.CausalContext);
    }


    //Declared before the generators that consume it: static initializers run in textual order.
    private static Gen<(int Replica, int Seed)> OpGen { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed));


    private static Gen<(OffsetAnchoredSequence<int> Full, OffsetAnchoredSequence<int> Prefix)> GenHistoryWithCut { get; } =
        Gen.Select(OpGen.Array[0, 8], Gen.Int[0, 8], static (ops, cut) =>
        {
            (OffsetAnchoredSequence<int> full, IReadOnlyList<OffsetAnchoredSequence<int>> snapshots) = BuildSnapshots(ops);

            return (full, SnapshotAt(snapshots, cut));
        });


    //Live-axis op histories over the EMPTY base, so an empty operand shares the generation: head and
    //live-anchored inserts plus dotted removes of still-visible elements.
    private static (OffsetAnchoredSequence<int> Full, IReadOnlyList<OffsetAnchoredSequence<int>> Snapshots) BuildSnapshots((int Replica, int Seed)[] ops)
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.Empty;
        var anchors = new List<OffsetAnchor>();
        var snapshots = new List<OffsetAnchoredSequence<int>>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = sequence.VisibleElements.Count;
            if(seed % 3 == 0 && visibleCount > 0)
            {
                OffsetAnchor target = sequence.VisibleElements[seed % visibleCount].Anchor;
                sequence = sequence.Remove(target, Replicas[replica]);
            }
            else if(anchors.Count == 0)
            {
                (sequence, OffsetAnchor head) = sequence.InsertAtHead((100 * replica) + opIndex, Replicas[replica]);
                anchors.Add(head);
            }
            else
            {
                (sequence, OffsetAnchor inserted) = sequence.InsertAfter(anchors[seed % anchors.Count], (100 * replica) + opIndex, Replicas[replica]);
                anchors.Add(inserted);
            }

            snapshots.Add(sequence);
        }

        return (sequence, snapshots);
    }


    private static OffsetAnchoredSequence<int> SnapshotAt(IReadOnlyList<OffsetAnchoredSequence<int>> snapshots, int cut)
    {
        int bounded = Math.Min(cut, snapshots.Count);

        return bounded == 0 ? OffsetAnchoredSequence<int>.Empty : snapshots[bounded - 1];
    }


    //Folds the shipped min-fold over one gossip digest per member context; distinct origins do not affect
    //the element-wise minimum but keep the digests honest.
    private static VectorClock FrontierOf(params VectorClock[] memberContexts)
    {
        var digests = new List<GossipDigest>(memberContexts.Length);
        for(int i = 0; i < memberContexts.Length; i++)
        {
            digests.Add(new GossipDigest(MakeReplica((byte)(200 + i)), memberContexts[i]));
        }

        return StabilityFrontier.Compute(digests);
    }


    private static DotState DotStateOf(Dot dot) => new(ImmutableArray.Create(dot.Replica.AsSpan()), dot.Counter);


    //A base slot's projection identity is the FULL 32-byte sentinel {254, 0x31 zeroes} with counter =
    //base offset + 1. Production ReplicaId has NO reserved range; non-collision rests on the sentinel's
    //entropy — a random 32-byte id equals it with negligible probability — and no code may ever detect
    //placeholders by their first byte.
    private static DotState SentinelDot(int offset)
    {
        Span<byte> sentinel = stackalloc byte[ReplicaId.Size];
        sentinel[0] = 254;

        return new DotState(ImmutableArray.Create(ReplicaId.FromSpan(sentinel).AsSpan()), offset + 1);
    }


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
