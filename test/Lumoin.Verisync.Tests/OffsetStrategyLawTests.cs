using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.OffsetV2"/> strategy with the shared law
/// harness. All generated operands share one base generation — cross-generation merging fails closed
/// by design and is exercised in the focused tests instead. Compaction requires an insert-quiescent
/// frontier (offset.v2 §17), so every generated case takes the shared prefix's OWN full context as the
/// frontier — every insert is certified — and confines divergence to REMOVES-ONLY suffixes: an
/// above-frontier remove keeps every vertex stable, so both operands stay quiescent and, by the
/// determinism inclusion, derive the SAME checkpoint at the shared frontier, while a suffix insert would
/// raise an unstable vertex and Compact would fail closed. Checkpoints are the prefix's certified
/// projection: real vertex dots for live elements, the full-32-byte sentinel identity for base slots.
/// </summary>
[TestClass]
internal sealed class OffsetStrategyLawTests: SequenceStrategyLawTests<OffsetAnchoredSequence<int>, int, OffsetAnchor>
{
    //Base values sit in their own range so anchors are recoverable by value in the focused tests; live
    //values use the per-replica ranges of the other generators.
    private static ImmutableArray<int> SharedBase { get; } = [9000, 9001, 9002];

    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2), MakeReplica(3)];


    protected override SequenceCrdtContext<OffsetAnchoredSequence<int>, int, OffsetAnchor> Context { get; } = WellKnownSequenceStrategies.CreateOffset<int>();


    protected override int FreshValue => -1;


    protected override Gen<OffsetAnchoredSequence<int>> GenFromReplica(int replicaIndex)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => Build(replicaIndex, seeds));
    }


    protected override ReplicaId Replica(int replicaIndex) => Replicas[replicaIndex];


    protected override OffsetAnchor AnchorOfVisibleElement(OffsetAnchoredSequence<int> sequence, int index)
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
        //a suffix insert would raise an unstable vertex and Compact would fail closed.
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(SharedBase);
        var targets = new List<OffsetAnchor>
        {
            OffsetAnchor.Head,
            OffsetAnchor.AtBase(0),
            OffsetAnchor.AtBase(1),
            OffsetAnchor.AtBase(2)
        };
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = shared.VisibleElements.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                OffsetAnchor removeTarget = shared.VisibleElements[seed % visibleCount].Anchor;
                shared = shared.Remove(removeTarget, Replicas[replica]);
            }
            else
            {
                OffsetAnchor inserted;
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
        var targets = new List<OffsetAnchor>
        {
            OffsetAnchor.Head,
            OffsetAnchor.AtBase(0),
            OffsetAnchor.AtBase(1),
            OffsetAnchor.AtBase(2)
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

            OffsetAnchor inserted;
            (sequence, inserted) = sequence.InsertAfter(targets[seed % targets.Count], value, Replicas[replicaIndex]);
            targets.Add(inserted);
            value++;
        }

        return sequence;
    }


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
