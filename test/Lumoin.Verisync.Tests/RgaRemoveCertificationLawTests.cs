using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The slice-1 remove-certification law suite for <see cref="Rga{TValue}"/>: a remove is a dotted event,
/// the frontier certifies it, and the drop gate additionally requires a certified remove-dot. Frontiers are
/// folded from REAL gossip digests over each member's <see cref="Rga{TValue}.CausalContext"/> — including
/// the laggard's — so the shipped <see cref="StabilityFrontier"/> min-fold is exercised end to end. The
/// certified projection at a frontier includes a locally tombstoned element whose remove is not yet
/// certified, which is why several checkpoints carry values the operand's own <c>Values</c> hides. The
/// four strategy-agnostic remove laws — LAW-RG, LAW-NR, LAW-SR, and LAW-NFD — have lifted into
/// <see cref="SequenceStrategyLawTests{TSequence, TValue, TAnchor}"/>; what remains here is the
/// RGA-specific translation-map, orphan-tombstone, tie-break, and deep-chain family.
/// </summary>
[TestClass]
internal sealed class RgaRemoveCertificationLawTests
{
    private static ReplicaId R1 { get; } = MakeReplica(1);
    private static ReplicaId R2 { get; } = MakeReplica(2);
    private static ReplicaId R3 { get; } = MakeReplica(3);
    private static ReplicaId R5 { get; } = MakeReplica(5);
    private static ReplicaId R10 { get; } = MakeReplica(10);

    //The replica axes the CsCheck op histories mint on; one replica per operand index.
    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2)];


    //LAW-TMC: two operands compacted at different honest frontiers carry different translation maps and drop
    //sets, yet their merge is order-independent on full state, resolves each dropped dot to its nearest
    //retained ancestor by the max-counter rule, and neither order throws.
    [TestMethod]
    public void TranslationMapsMergeCommutatively()
    {
        //Chain a <- P <- D on R1; R2 removes D (dot d1), then P (dot d2); every member observes both.
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withP, Dot idP) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withD, Dot idD) = withP.InsertAfter(idP, 3, R1);
        Rga<int> withDRemoved = withD.Remove(idD, R2);
        Rga<int> withBothRemoved = withDRemoved.Remove(idP, R2);

        //F1 covers the inserts and d1 but not d2; F2 covers everything.
        VectorClock f1 = FrontierOf(withBothRemoved.CausalContext, withDRemoved.CausalContext);
        VectorClock f2 = FrontierOf(withBothRemoved.CausalContext, withBothRemoved.CausalContext);

        //A drops D only (P is a tombstoned ghost whose remove is not yet certified at F1).
        ImmutableArray<SequenceCheckpointEntry<int>> checkpointA = withBothRemoved.CertifiedProjection(f1);
        Rga<int> a = withBothRemoved.Compact(f1, checkpointA);

        //B drops D and P.
        ImmutableArray<SequenceCheckpointEntry<int>> checkpointB = withBothRemoved.CertifiedProjection(f2);
        Rga<int> b = withBothRemoved.Compact(f2, checkpointB);

        Rga<int> forward = a.Merge(b);
        Rga<int> backward = b.Merge(a);
        Assert.AreEqual(forward, backward);

        //D is dropped in both operands, so the merged map serves it; the max-counter rule picks the nearer
        //retained ancestor P over a. TranslateAnchor(D) agrees across both merge orders.
        Assert.AreEqual(idP, forward.TranslateAnchor(idD));
        Assert.AreEqual(idP, backward.TranslateAnchor(idD));

        //P re-enters as a live ghost, so it serves itself and its latent P->a map entry is shadowed.
        //Compacting the merge at F2 certifies P's remove, drops it, and the composed map then routes both D
        //and P through to a — the max-counter resolution held.
        Rga<int> recompacted = forward.Compact(f2, checkpointB);
        Assert.AreEqual(idA, recompacted.TranslateAnchor(idP));
        Assert.AreEqual(idA, recompacted.TranslateAnchor(idD));
    }


    //Two operands carry the SAME insert dot with DIFFERENT vertex content — a forged double-mint. A dot mints
    //exactly one immutable vertex, so Merge fails closed in both orders on the equivocation detector rather
    //than letting merge order silently pick a value; an identical-vertex pair carries no conflict and merges.
    [TestMethod]
    public void ConflictingVerticesUnderOneInsertIdentityFailClosedOnMerge()
    {
        //The context covers the shared head dot (R1,1); one state carries it with value 1, the other value 2.
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        ImmutableArray<RgaVertexEntry<int>> headWithOne = [new RgaVertexEntry<int>(DotStateOf(new Dot(R1, 1)), null, 1)];
        ImmutableArray<RgaVertexEntry<int>> headWithTwo = [new RgaVertexEntry<int>(DotStateOf(new Dot(R1, 1)), null, 2)];
        ImmutableArray<RgaTombstoneEntry> noTombstones = [];

        Rga<int> carriesOne = Rga<int>.FromState(new RgaState<int>(context, headWithOne, noTombstones));
        Rga<int> carriesTwo = Rga<int>.FromState(new RgaState<int>(context, headWithTwo, noTombstones));

        Assert.ThrowsExactly<InvalidOperationException>(() => carriesOne.Merge(carriesTwo));
        Assert.ThrowsExactly<InvalidOperationException>(() => carriesTwo.Merge(carriesOne));

        //An identical-vertex pair is the ordinary idempotent union — no conflict, no throw.
        Rga<int> carriesOneTwin = Rga<int>.FromState(new RgaState<int>(context, headWithOne, noTombstones));
        Assert.AreEqual(carriesOne, carriesOne.Merge(carriesOneTwin));
    }


    //G2: the property-based companion of LAW-TMC. Two operands are the full shared state compacted at two
    //honest historical frontiers, so they carry genuinely different maps and drop sets; the merge commutes on
    //full state, neither order throws (both hold every tombstone), and TranslateAnchor agrees across orders.
    [TestMethod]
    public void TranslationMapsMergeCommutativelyAcrossGeneratedFrontiers()
    {
        GenTwoFrontierOperands.Sample(input =>
        {
            Rga<int> forward = input.A.Merge(input.B);
            Rga<int> backward = input.B.Merge(input.A);
            Assert.AreEqual(forward, backward);
            foreach(Dot key in input.Keys)
            {
                Assert.AreEqual(forward.TranslateAnchor(key), backward.TranslateAnchor(key));
            }
        });
    }


    //MANDATED REGRESSION (A-killer): a concurrent insert after a tombstoned element must not be re-parented by
    //a covered-absent merge-drop. The sibling d sits strictly between x and c in counter order, so keeping the
    //ghost yields a,d,c while re-parenting c onto a would yield a,c,d — the assertion discriminates the two
    //designs on the visible ORDER, exactly the region where a merge-drop "optimization" regresses ordering.
    [TestMethod]
    public void AConcurrentInsertAfterATombstonedElementSurvivesCompactMergeCommutation()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withX, Dot idX) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withD, _) = withX.InsertAfter(idA, 3, R1);
        Rga<int> observed = withD.Remove(idX, R1);

        //R3 observed x and its remove, then inserted c after the tombstoned x; c is above the honest frontier.
        (Rga<int> r3State, _) = observed.InsertAfter(idX, 4, R3);

        //Peer P compacted childless-x at the honest frontier (which certifies x's remove) before c existed.
        VectorClock frontier = observed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = observed.CertifiedProjection(frontier);
        Rga<int> peer = observed.Compact(frontier, checkpoint);

        //The reference is the compaction of the merged uncompacted state; x stays a ghost, so c holds its
        //place UNDER the ghost — after d, not before it. A covered-absent re-parent would produce [1, 4, 3].
        Rga<int> reference = observed.Merge(r3State).Compact(frontier, checkpoint);
        int[] expected = [1, 3, 4];
        Assert.AreSequenceEqual(expected, reference.Values.ToArray());
        Assert.AreSequenceEqual(expected, peer.Merge(r3State).Values.ToArray());
        Assert.AreSequenceEqual(expected, r3State.Merge(peer).Values.ToArray());
    }


    //MANDATED REGRESSION (C-killer), impossibility half: for honest multi-member histories, after compacting
    //at a frontier folded from ALL members' digests, every member either lacks each dropped vertex or holds
    //its dotted tombstone — a compacted element is never held live by an honest peer.
    [TestMethod]
    public void ACompactedElementIsNeverHeldLiveByAnHonestPeer()
    {
        GenHonestHistory.Sample(input =>
        {
            var contexts = new List<VectorClock>(input.Members.Count);
            foreach(Rga<int> member in input.Members)
            {
                contexts.Add(member.CausalContext);
            }

            VectorClock frontier = FrontierFromContexts(contexts);
            ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = input.Full.CertifiedProjection(frontier);
            Rga<int> compacted = input.Full.Compact(frontier, checkpoint);

            foreach(RgaVertexEntry<int> vertex in input.Full.ToState().Vertices)
            {
                Dot dot = DotOf(vertex.Id);
                if(!IsDropped(compacted, dot))
                {
                    continue;
                }

                foreach(Rga<int> member in input.Members)
                {
                    Assert.IsTrue(!HasVertex(member, dot) || HasDottedTombstone(member, dot));
                }
            }
        });
    }


    //MANDATED REGRESSION (C-killer), honest deterministic half: a laggard that observed the remove holds x
    //tombstoned and inserts c after it; merging the peer that compacted childless-x re-enters x as a ghost
    //WITH its tombstone, so c stays ordered UNDER the ghost — after the sibling d — in both merge orders. A
    //re-parenting merge-drop would move c ahead of d, which the explicit order assertion rejects.
    [TestMethod]
    public void AnHonestLaggardMergeReEntersTheGhostWithItsTombstone()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withX, Dot idX) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withD, _) = withX.InsertAfter(idA, 3, R1);
        Rga<int> observed = withD.Remove(idX, R1);
        (Rga<int> laggard, _) = observed.InsertAfter(idX, 4, R3);

        VectorClock frontier = observed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = observed.CertifiedProjection(frontier);
        Rga<int> peer = observed.Compact(frontier, checkpoint);

        Rga<int> forward = peer.Merge(laggard);
        Rga<int> backward = laggard.Merge(peer);

        //a, d, and c are visible with c under the ghost x; x re-enters hidden.
        int[] expected = [1, 3, 4];
        Assert.AreSequenceEqual(expected, forward.Values.ToArray());
        Assert.AreSequenceEqual(expected, backward.Values.ToArray());
        Assert.AreEqual(3, forward.Count);
        Assert.AreEqual(3, backward.Count);
    }


    //G4: a 10,000-deep tombstoned run must classify iteratively without stack growth. The whole run sits after
    //a retained head element, so it is non-head; every element's remove is certified at the state's own
    //context, and every dropped dot translates to the head.
    [TestMethod]
    public void ADeepTombstoneRunCompactsWithoutRecursion()
    {
        const int Depth = 10_000;

        //Built in one FromState pass to avoid quadratic immutable rebuilds. The head a=(R1,1) is retained; the
        //chain (R1,2..Depth+1) is tombstoned by dotted removes on R2's axis.
        Dot idA = new(R1, 1);
        ImmutableArray<RgaVertexEntry<int>>.Builder vertices = ImmutableArray.CreateBuilder<RgaVertexEntry<int>>(Depth + 1);
        vertices.Add(new RgaVertexEntry<int>(DotStateOf(idA), null, 0));
        ImmutableArray<RgaTombstoneEntry>.Builder tombstones = ImmutableArray.CreateBuilder<RgaTombstoneEntry>(Depth);
        for(int i = 1; i <= Depth; i++)
        {
            vertices.Add(new RgaVertexEntry<int>(DotStateOf(new Dot(R1, i + 1)), DotStateOf(new Dot(R1, i)), i));
            tombstones.Add(new RgaTombstoneEntry(DotStateOf(new Dot(R1, i + 1)), [DotStateOf(new Dot(R2, i))]));
        }

        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), Depth + 1), new ReplicaCounterEntry(Bytes(R2), Depth)]);
        Rga<int> sequence = Rga<int>.FromState(new RgaState<int>(context, vertices.ToImmutable(), tombstones.ToImmutable()));

        VectorClock frontier = sequence.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = sequence.CertifiedProjection(frontier);
        Rga<int> compacted = sequence.Compact(frontier, checkpoint);

        int[] expected = [0];
        Assert.AreSequenceEqual(expected, compacted.Values.ToArray());

        bool everyDroppedDotTranslatesToHead = true;
        for(int i = 1; i <= Depth; i++)
        {
            if(!idA.Equals(compacted.TranslateAnchor(new Dot(R1, i + 1))))
            {
                everyDroppedDotTranslatesToHead = false;

                break;
            }
        }

        Assert.IsTrue(everyDroppedDotTranslatesToHead);
    }


    //G3/§2.6: a UI-stale remove on a compacted state mints an orphan tombstone (its target is only a
    //translation key). The orphan is outside retention, so it survives a further compaction and permanently
    //masks an honest ghost re-add in both merge orders.
    [TestMethod]
    public void ARemoveAfterCompactionLeavesAPermanentMaskingOrphanTombstone()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> ghostHolder = withB.Remove(idB, R1);

        VectorClock frontier = ghostHolder.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = ghostHolder.CertifiedProjection(frontier);
        Rga<int> compacted = ghostHolder.Compact(frontier, checkpoint);

        //idB is now only a translation key, so this mints an orphan tombstone with a fresh R2 remove-dot.
        Rga<int> afterOrphanRemove = compacted.Remove(idB, R2);

        //A further compaction keeps the orphan (orphans are outside retention).
        VectorClock orphanFrontier = afterOrphanRemove.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> orphanCheckpoint = afterOrphanRemove.CertifiedProjection(orphanFrontier);
        Rga<int> compactedAgain = afterOrphanRemove.Compact(orphanFrontier, orphanCheckpoint);

        //The orphan masks an honest ghost re-add: merging the ghost-holder keeps b hidden in both orders.
        int[] masked = [1];
        Assert.AreSequenceEqual(masked, compactedAgain.Merge(ghostHolder).Values.ToArray());
        Assert.AreSequenceEqual(masked, ghostHolder.Merge(compactedAgain).Values.ToArray());
        Assert.AreEqual(1, compactedAgain.Merge(ghostHolder).Count);
    }


    //The gate-1 pinned trace (T1): a remove on the shared element's own axis raises the counter max, which
    //flips the descending (counter, replica) tie-break between concurrent sibling inserts. Convergence holds
    //in both scenarios; only the concurrent tie-break moves.
    [TestMethod]
    public void RemoveNudgesTheConcurrentSiblingTieBreak()
    {
        //R5 authors A at head; R10 merges, so both hold context {R5:1}.
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R5);

        //Without a remove: C1=(R10,2) and C2=(R5,2) tie on counter, R10 outranks R5, so the order is A,C1,C2.
        (Rga<string> r10NoRemove, _) = withA.InsertAfter(idA, "C1", R10);
        (Rga<string> r5NoRemove, _) = withA.InsertAfter(idA, "C2", R5);
        string[] withoutRemove = ["A", "C1", "C2"];
        Assert.AreSequenceEqual(withoutRemove, r10NoRemove.Merge(r5NoRemove).Values.ToArray());
        Assert.AreSequenceEqual(withoutRemove, r5NoRemove.Merge(r10NoRemove).Values.ToArray());

        //With the remove: R5 removes A first (Increment mints remove-dot (R5,2), raising the max to 2), then
        //inserts C2 after the tombstoned A (IncrementPastAll mints (R5,3)), which now outranks C1=(R10,2).
        Rga<string> r5Removed = withA.Remove(idA, R5);
        (Rga<string> r5WithC2, _) = r5Removed.InsertAfter(idA, "C2", R5);
        (Rga<string> r10WithC1, _) = withA.InsertAfter(idA, "C1", R10);
        string[] withRemove = ["C2", "C1"];
        Assert.AreSequenceEqual(withRemove, r5WithC2.Merge(r10WithC1).Values.ToArray());
        Assert.AreSequenceEqual(withRemove, r10WithC1.Merge(r5WithC2).Values.ToArray());
    }


    //LAW-OT: an orphan tombstone — a remove of a dot the remover never held — hides its target the instant
    //the insert arrives. R2 removes the winner-inserted head dot it learned out of band; merging the
    //insert-holder in either order hides the element immediately and the orders converge, and because the
    //target is head-anchored the ghost is retained through a certifying compaction — the orphan survives.
    [TestMethod]
    public void AnOrphanTombstoneHidesItsTargetTheMomentTheInsertArrives()
    {
        //The insert lives in a sibling state; the orphan-holder removes the same dot it never held.
        (Rga<int> insertHolder, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> orphanHolder = Rga<int>.Empty.Remove(idA, R2);

        Rga<int> forward = orphanHolder.Merge(insertHolder);
        Rga<int> backward = insertHolder.Merge(orphanHolder);

        //The target is hidden the moment the insert arrives, in both orders, and the orders converge.
        Assert.HasCount(0, forward.Values);
        Assert.HasCount(0, backward.Values);
        Assert.AreEqual(0, forward.Count);
        Assert.AreEqual(forward, backward);

        //The remove is certified at the merged context, yet the head-anchored ghost is retained, so the orphan
        //survives the compaction and keeps masking its target.
        VectorClock frontier = forward.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = forward.CertifiedProjection(frontier);
        Assert.HasCount(0, checkpoint);
        Rga<int> compacted = forward.Compact(frontier, checkpoint);
        Assert.HasCount(0, compacted.Values);
        Assert.AreEqual(idA, compacted.TranslateAnchor(idA));
    }


    //G5: a silent member (an empty summary) folds the frontier to empty, so a compaction there drops nothing
    //and returns this; a partially-silent member pins exactly one axis, and a remove certified on another axis
    //still cannot drop an element whose insert sits on the pinned axis. Deterministic, real StabilityFrontier.
    [TestMethod]
    public void ASilentMemberPinsTheFrontierAndForbidsEveryDrop()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        //One member is silent (VectorClock.Empty), so the min-fold floors every axis at zero: the frontier is
        //empty, its projection is empty, and the compaction drops nothing.
        VectorClock silentFrontier = FrontierOf(removed.CausalContext, VectorClock.Empty);
        ImmutableArray<SequenceCheckpointEntry<int>> silentCheckpoint = removed.CertifiedProjection(silentFrontier);
        Assert.HasCount(0, silentCheckpoint);
        Rga<int> underSilence = removed.Compact(silentFrontier, silentCheckpoint);
        Assert.AreEqual(removed, underSilence);
        Assert.AreEqual(idB, underSilence.TranslateAnchor(idB));

        //A remove whose INSERT sits on the pinned axis cannot drop: R2 removes an R1-inserted element, and a
        //member behind on the R1 axis pins it below the insert, so the certified R2 remove still cannot fold it.
        (Rga<int> withC, Dot idC) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withD, Dot idD) = withC.InsertAfter(idC, 2, R1);
        Rga<int> removedByR2 = withD.Remove(idD, R2);
        VectorClock behindOnR1 = VectorClock.Empty.Increment(R1).Increment(R2);
        VectorClock partialFrontier = FrontierOf(removedByR2.CausalContext, behindOnR1);
        ImmutableArray<SequenceCheckpointEntry<int>> partialCheckpoint = removedByR2.CertifiedProjection(partialFrontier);
        Rga<int> underPartialSilence = removedByR2.Compact(partialFrontier, partialCheckpoint);
        Assert.AreEqual(removedByR2, underPartialSilence);
        Assert.AreEqual(idD, underPartialSilence.TranslateAnchor(idD));
    }


    //Declared before the generators that consume it: static initializers run in textual order.
    private static Gen<(int Replica, int Seed)> OpGen { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed));


    //Filtered so every sampled case actually drops a vertex at the folded frontier — without the filter the
    //assertion loop is empty in the overwhelming majority of random histories and the law tests nothing.
    private static Gen<(Rga<int> Full, Rga<int> PrefixAtCut, IReadOnlyList<Rga<int>> Members)> GenHonestHistory { get; } =
        Gen.Select(OpGen.Array[0, 8], Gen.Int[0, 8], static (ops, cut) =>
        {
            (Rga<int> full, IReadOnlyList<Rga<int>> snapshots) = BuildSnapshots(ops);
            Rga<int> prefixAtCut = SnapshotAt(snapshots, cut);
            var members = new List<Rga<int>> { prefixAtCut, full };
            int boundedCut = Math.Min(cut, snapshots.Count);
            if(snapshots.Count > boundedCut)
            {
                //A middle member between the cut and the full state; still dominates the cut so the min holds.
                members.Add(snapshots[(boundedCut + snapshots.Count) / 2]);
            }

            return (Full: full, PrefixAtCut: prefixAtCut, Members: (IReadOnlyList<Rga<int>>)members);
        }).Where(static input => AnyVertexDrops(input.Full, input.PrefixAtCut));


    private static Gen<(Rga<int> A, Rga<int> B, IReadOnlyList<Dot> Keys)> GenTwoFrontierOperands { get; } =
        Gen.Select(OpGen.Array[0, 8], Gen.Int[0, 8], Gen.Int[0, 8], static (ops, cut1, cut2) =>
        {
            (Rga<int> full, IReadOnlyList<Rga<int>> snapshots) = BuildSnapshots(ops);
            Rga<int> prefixLo = SnapshotAt(snapshots, Math.Min(cut1, cut2));
            Rga<int> prefixHi = SnapshotAt(snapshots, Math.Max(cut1, cut2));

            //Two honest historical frontiers: each operand is the full state compacted at a prefix cut, so the
            //checkpoint is that prefix's dotted certified projection.
            Rga<int> a = full.Compact(prefixLo.CausalContext, full.CertifiedProjection(prefixLo.CausalContext));
            Rga<int> b = full.Compact(prefixHi.CausalContext, full.CertifiedProjection(prefixHi.CausalContext));

            var keys = new List<Dot>();
            var seen = new HashSet<Dot>();
            foreach(RgaVertexEntry<int> vertex in full.ToState().Vertices)
            {
                Dot dot = DotOf(vertex.Id);
                if((IsDropped(a, dot) || IsDropped(b, dot)) && seen.Add(dot))
                {
                    keys.Add(dot);
                }
            }

            return (A: a, B: b, Keys: (IReadOnlyList<Dot>)keys);
            //Filtered to cases with a real dropped dot AND genuinely different operands — otherwise the law
            //degenerates to merge idempotence and never exercises cross-frontier translation resolution.
        }).Where(static input => input.Keys.Count > 0 && !input.A.Equals(input.B));


    private static (Rga<int> Full, IReadOnlyList<Rga<int>> Snapshots) BuildSnapshots((int Replica, int Seed)[] ops)
    {
        Rga<int> sequence = Rga<int>.Empty;
        var insertedDots = new List<Dot>();
        var snapshots = new List<Rga<int>>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = sequence.Values.Count;
            if(seed % 3 == 0 && visibleCount > 0)
            {
                //A dotted remove by the op's replica, biased to the most recently inserted still-visible
                //element: a recent insert is most likely childless, which is the only kind of tombstone the
                //drop gate can ever fold, so generated histories actually enter the compaction region.
                Dot target = MostRecentVisibleDot(sequence, insertedDots);
                sequence = sequence.Remove(target, Replicas[replica]);
            }
            else if(insertedDots.Count == 0)
            {
                (sequence, Dot head) = sequence.InsertAtHead((100 * replica) + opIndex, Replicas[replica]);
                insertedDots.Add(head);
            }
            else
            {
                (sequence, Dot inserted) = sequence.InsertAfter(insertedDots[seed % insertedDots.Count], (100 * replica) + opIndex, Replicas[replica]);
                insertedDots.Add(inserted);
            }

            snapshots.Add(sequence);
        }

        return (sequence, snapshots);
    }


    private static Rga<int> SnapshotAt(IReadOnlyList<Rga<int>> snapshots, int cut)
    {
        int bounded = Math.Min(cut, snapshots.Count);

        return bounded == 0 ? Rga<int>.Empty : snapshots[bounded - 1];
    }


    private static Dot MostRecentVisibleDot(Rga<int> sequence, List<Dot> insertedDots)
    {
        var hidden = new HashSet<Dot>();
        foreach(RgaTombstoneEntry tombstone in sequence.ToState().Tombstones)
        {
            hidden.Add(DotOf(tombstone.Target));
        }

        for(int i = insertedDots.Count - 1; i >= 0; i--)
        {
            if(!hidden.Contains(insertedDots[i]))
            {
                return insertedDots[i];
            }
        }

        throw new InvalidOperationException("No visible element remains to remove.");
    }


    //The frontier the honest-history law folds is the cut member's context (it is dominated element-wise by
    //every other member), so the predicate compacts at exactly the frontier the law will use.
    private static bool AnyVertexDrops(Rga<int> full, Rga<int> prefixAtCut)
    {
        Rga<int> compacted = full.Compact(prefixAtCut.CausalContext, full.CertifiedProjection(prefixAtCut.CausalContext));
        foreach(RgaVertexEntry<int> vertex in full.ToState().Vertices)
        {
            if(IsDropped(compacted, DotOf(vertex.Id)))
            {
                return true;
            }
        }

        return false;
    }


    private static bool IsDropped(Rga<int> compacted, Dot dot)
    {
        Dot? translated = compacted.TranslateAnchor(dot);

        return translated is not null && !translated.Equals(dot);
    }


    private static bool HasVertex(Rga<int> member, Dot dot)
    {
        foreach(RgaVertexEntry<int> vertex in member.ToState().Vertices)
        {
            if(DotOf(vertex.Id).Equals(dot))
            {
                return true;
            }
        }

        return false;
    }


    private static bool HasDottedTombstone(Rga<int> member, Dot dot)
    {
        foreach(RgaTombstoneEntry tombstone in member.ToState().Tombstones)
        {
            if(tombstone.RemoveDots.Length > 0 && DotOf(tombstone.Target).Equals(dot))
            {
                return true;
            }
        }

        return false;
    }


    private static Dot DotOfValue(Rga<int> sequence, int value)
    {
        foreach(RgaVertexEntry<int> vertex in sequence.ToState().Vertices)
        {
            if(vertex.Value == value)
            {
                return DotOf(vertex.Id);
            }
        }

        throw new InvalidOperationException("The value was not found among the vertices.");
    }


    //Folds the shipped min-fold over one gossip digest per member context; distinct origins do not affect the
    //element-wise minimum but keep the digests honest.
    private static VectorClock FrontierOf(params VectorClock[] memberContexts) => FrontierFromContexts(memberContexts);


    private static VectorClock FrontierFromContexts(IReadOnlyList<VectorClock> memberContexts)
    {
        var digests = new List<GossipDigest>(memberContexts.Count);
        for(int i = 0; i < memberContexts.Count; i++)
        {
            digests.Add(new GossipDigest(MakeReplica((byte)(200 + i)), memberContexts[i]));
        }

        return StabilityFrontier.Compute(digests);
    }


    private static Dot DotOf(DotState state) => new(ReplicaId.FromSpan(state.Replica.AsSpan()), state.Counter);


    private static DotState DotStateOf(Dot dot) => new(Bytes(dot.Replica), dot.Counter);


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
