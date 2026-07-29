using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.OffsetV2"/> strategy with the shared law
/// harness under its public addressing type <see cref="OffsetAddress"/>. All generated operands share
/// one base generation — cross-generation merging fails closed by design and is exercised in the
/// focused tests instead. Compaction requires an insert-quiescent frontier (offset.v2 §17), so every
/// generated case takes the shared prefix's OWN full context as the frontier — every insert is
/// certified — and confines divergence to REMOVES-ONLY suffixes: an above-frontier remove keeps every
/// vertex stable, so both operands stay quiescent and, by the determinism inclusion, derive the SAME
/// checkpoint at the shared frontier, while a suffix insert would raise an unstable vertex and Compact
/// would fail closed. Checkpoints are the prefix's certified projection: real vertex dots for live
/// elements, the full-32-byte sentinel identity for base slots. The anchors law then covers the base
/// window by right: a visible pre-compaction address is a prior-generation address the map arm serves.
/// </summary>
[TestClass]
internal sealed class OffsetStrategyLawTests: SequenceStrategyLawTests<OffsetAnchoredSequence<int>, int, OffsetAddress>
{
    //Base values sit in their own range so anchors are recoverable by value in the focused tests; live
    //values use the per-replica ranges of the other generators.
    private static ImmutableArray<int> SharedBase { get; } = [9000, 9001, 9002];

    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2), MakeReplica(3)];

    private static ReplicaId R1 { get; } = MakeReplica(1);


    protected override SequenceCrdtContext<OffsetAnchoredSequence<int>, int, OffsetAddress> Context { get; } = WellKnownSequenceStrategies.CreateOffset<int>();


    protected override int FreshValue => -1;


    protected override Gen<OffsetAnchoredSequence<int>> GenFromReplica(int replicaIndex)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => Build(replicaIndex, seeds));
    }


    protected override ReplicaId Replica(int replicaIndex) => Replicas[replicaIndex];


    protected override OffsetAddress AnchorOfVisibleElement(OffsetAnchoredSequence<int> sequence, int index)
    {
        return sequence.VisibleElements[index].Anchor;
    }


    protected override Gen<(OffsetAnchoredSequence<int> Sequence, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)>? GenCompactionCase
    {
        get
        {
            return GenCase.Select(static input => (input.A.Merge(input.B), input.Frontier, input.Checkpoint));
        }
    }


    protected override Gen<(OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)>? GenCommutationCase => GenCase;


    //LAW-NFD: an op history of inserts and dotted removes over the EMPTY base, so an empty operand shares
    //the genesis generation with the Full and Behind states and no generation fence throws.
    protected override Gen<(OffsetAnchoredSequence<int> Full, OffsetAnchoredSequence<int> Behind)> GenFullAndBehindHistory { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 8],
            Gen.Int[0, 8],
            static (ops, cut) =>
            {
                (OffsetAnchoredSequence<int> full, IReadOnlyList<OffsetAnchoredSequence<int>> snapshots) = BuildSnapshots(ops);

                return (Full: full, Behind: SnapshotAt(snapshots, cut));
            }).Where(static pair => !pair.Full.Equals(pair.Behind));


    //The drop-only remove scenario for LAW-NR/LAW-SR: the survivor is a BASE slot and the removed element
    //is a childless live vertex after it. Compacting at the certified frontier DROPS the vertex without
    //touching the base, so the generation stays genesis and the compacted state merges legally with the
    //uncompacted ghost-holder and stale operands. A live-survivor construction would advance the generation
    //and throw the fence.
    protected override RemoveScenario? BuildRemoveScenario()
    {
        ImmutableArray<int> singleBase = [9000];
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(singleBase);
        (shared, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 42, R1);
        OffsetAnchoredSequence<int> stalePreRemove = shared;
        OffsetAnchoredSequence<int> ghostHolder = shared.Remove(x, R1);

        VectorClock postRemove = ghostHolder.CausalContext;
        VectorClock frontier = FrontierOf(postRemove, postRemove, postRemove);
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = ghostHolder.CertifiedProjection(frontier);
        OffsetAnchoredSequence<int> compacted = ghostHolder.Compact(frontier, checkpoint);

        return new RemoveScenario(compacted, ghostHolder, stalePreRemove, frontier, checkpoint);
    }


    //The offset-shaped half of LAW-RG under the generic live-survivor construction (empty base, both values
    //the sentinel): the survivor converts to base offset 0 in BOTH halves, so BOTH stamp the frontier and
    //advance to generation 1. At the uncertified frontier the removed element converts pending-removed to
    //offset 1, hidden; at the certified frontier it drops and the removed anchor translates to the gap at
    //offset 0. Each translated result is a base address of the current generation.
    protected override void AssertRemoveConversionOutcome(
        OffsetAnchoredSequence<int> uncertifiedCompacted,
        OffsetAnchoredSequence<int> certifiedCompacted,
        OffsetAddress removedAnchor,
        OffsetAddress survivorAnchor,
        VectorClock uncertifiedFrontier,
        VectorClock certifiedFrontier)
    {
        int[] uncertifiedBase = [FreshValue, FreshValue];
        Assert.AreSequenceEqual(uncertifiedBase, uncertifiedCompacted.ToState().Base.ToArray());
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(1), 1), uncertifiedCompacted.TranslateAnchor(removedAnchor));
        Assert.AreEqual(uncertifiedFrontier, VectorClock.FromState(uncertifiedCompacted.ToState().BaseFrontier));
        OffsetBaseRemovalEntry marking = uncertifiedCompacted.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, marking.Offset);
        Assert.HasCount(1, marking.RemoveDots);

        int[] certifiedBase = [FreshValue];
        Assert.AreSequenceEqual(certifiedBase, certifiedCompacted.ToState().Base.ToArray());
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 1), certifiedCompacted.TranslateAnchor(removedAnchor));
        Assert.AreEqual(certifiedFrontier, VectorClock.FromState(certifiedCompacted.ToState().BaseFrontier));
    }


    //The shared-prefix ops and the two divergent remove-only suffixes all come from CsCheck seeds, so
    //the whole case is deterministic and reproduces on shrink. Filtered so EVERY sampled case enters the
    //compaction region — something converts or drops at the frontier — the slice-1 lesson that unfiltered
    //histories rarely reach the region the laws exist to test.
    private static Gen<(OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)> GenCase { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 6],
            Gen.Int[0, 100].Array[0, 4],
            Gen.Int[0, 100].Array[0, 4],
            static (ops, suffixA, suffixB) => BuildCase(ops, suffixA, suffixB))
        .Where(static input => CompactionChangesTheMergedState(input));


    private static (OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint) BuildCase((int Replica, int Seed)[] ops, int[] suffixA, int[] suffixB)
    {
        //Shared prefix on the common base, inserts and dotted removes both, replayed into one sequence.
        //The compaction frontier is the prefix's OWN full context, so every insert is certified: the
        //merged state is insert-quiescent, the §17 contract compaction requires. Divergence lands only in
        //the suffixes, which are REMOVES-ONLY — an above-frontier remove keeps every vertex stable, while
        //a suffix insert would raise an unstable vertex and Compact would fail closed. The prefix is never
        //compacted while it is built, so its base and head seeds are generation-0 addresses.
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(SharedBase);
        var targets = new List<OffsetAddress>
        {
            new OffsetAddress(OffsetAnchor.Head, 0),
            new OffsetAddress(OffsetAnchor.AtBase(0), 0),
            new OffsetAddress(OffsetAnchor.AtBase(1), 0),
            new OffsetAddress(OffsetAnchor.AtBase(2), 0)
        };
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = shared.VisibleElements.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                OffsetAddress removeTarget = shared.VisibleElements[seed % visibleCount].Anchor;
                shared = shared.Remove(removeTarget, Replicas[replica]);
            }
            else
            {
                OffsetAddress inserted;
                (shared, inserted) = shared.InsertAfter(targets[seed % targets.Count], (100 * replica) + opIndex, Replicas[replica]);
                targets.Add(inserted);
            }
        }

        //The frontier is the shared prefix's own full context: it certifies every insert AND every shared
        //remove. The checkpoint is that prefix's certified projection, which the determinism theorem makes
        //the merged operand's projection at the same frontier too — the divergent suffix removes stay
        //above it. Real dots for live elements, sentinel identities for base slots.
        VectorClock frontier = shared.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = shared.CertifiedProjection(frontier);

        //Divergent REMOVES-ONLY suffixes above the frontier: each hides a still-visible element without
        //minting a vertex, so both operands stay insert-quiescent and derive the identical checkpoint.
        OffsetAnchoredSequence<int> a = ApplyRemoveSuffix(shared, Replicas[0], suffixA);
        OffsetAnchoredSequence<int> b = ApplyRemoveSuffix(shared, Replicas[1], suffixB);

        return (a, b, frontier, checkpoint);
    }


    private static bool CompactionChangesTheMergedState((OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint) input)
    {
        OffsetAnchoredSequence<int> merged = input.A.Merge(input.B);

        return !merged.Compact(input.Frontier, input.Checkpoint).Equals(merged);
    }


    //Removes-only, so the operand stays insert-quiescent: each seed hides one still-visible element,
    //minting an above-frontier remove-dot but never a vertex. A suffix insert would raise an unstable
    //vertex and Compact would fail closed under the §17 guard.
    private static OffsetAnchoredSequence<int> ApplyRemoveSuffix(OffsetAnchoredSequence<int> start, ReplicaId replica, int[] seeds)
    {
        OffsetAnchoredSequence<int> sequence = start;
        foreach(int seed in seeds)
        {
            int visibleCount = sequence.VisibleElements.Count;
            if(visibleCount == 0)
            {
                break;
            }

            sequence = sequence.Remove(sequence.VisibleElements[seed % visibleCount].Anchor, replica);
        }

        return sequence;
    }


    private static OffsetAnchoredSequence<int> Build(int replicaIndex, int[] seeds)
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(SharedBase);
        var targets = new List<OffsetAddress>
        {
            new OffsetAddress(OffsetAnchor.Head, 0),
            new OffsetAddress(OffsetAnchor.AtBase(0), 0),
            new OffsetAddress(OffsetAnchor.AtBase(1), 0),
            new OffsetAddress(OffsetAnchor.AtBase(2), 0)
        };
        int value = replicaIndex * 1000;

        foreach(int seed in seeds)
        {
            int visibleCount = sequence.VisibleElements.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                sequence = sequence.Remove(sequence.VisibleElements[seed % visibleCount].Anchor, Replicas[replicaIndex]);

                continue;
            }

            OffsetAddress inserted;
            (sequence, inserted) = sequence.InsertAfter(targets[seed % targets.Count], value, Replicas[replicaIndex]);
            targets.Add(inserted);
            value++;
        }

        return sequence;
    }


    //Live-axis op histories over the EMPTY base, so an empty operand shares the generation: head and
    //live-anchored inserts plus dotted removes of still-visible elements.
    private static (OffsetAnchoredSequence<int> Full, IReadOnlyList<OffsetAnchoredSequence<int>> Snapshots) BuildSnapshots((int Replica, int Seed)[] ops)
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.Empty;
        var anchors = new List<OffsetAddress>();
        var snapshots = new List<OffsetAnchoredSequence<int>>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = sequence.VisibleElements.Count;
            if(seed % 3 == 0 && visibleCount > 0)
            {
                OffsetAddress target = sequence.VisibleElements[seed % visibleCount].Anchor;
                sequence = sequence.Remove(target, Replicas[replica]);
            }
            else if(anchors.Count == 0)
            {
                (sequence, OffsetAddress head) = sequence.InsertAtHead((100 * replica) + opIndex, Replicas[replica]);
                anchors.Add(head);
            }
            else
            {
                (sequence, OffsetAddress inserted) = sequence.InsertAfter(anchors[seed % anchors.Count], (100 * replica) + opIndex, Replicas[replica]);
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


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
