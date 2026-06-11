using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.OffsetV1"/> strategy with the shared law
/// harness. All generated operands share one base generation — cross-generation merging fails closed
/// by design and is exercised in the focused tests instead.
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


    protected override Gen<(OffsetAnchoredSequence<int> Sequence, VectorClock Frontier, ImmutableArray<int> Checkpoint)>? GenCompactionCase
    {
        get
        {
            return GenCase.Select(static input => (input.A.Merge(input.B), input.Frontier, input.Checkpoint));
        }
    }


    protected override Gen<(OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint)>? GenCommutationCase => GenCase;


    //The shared-prefix ops, the frontier cut, and the two insert-only divergent suffixes all come from
    //CsCheck seeds, so the whole case is deterministic and reproduces on shrink.
    private static Gen<(OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint)> GenCase { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 6],
            Gen.Int[0, 6],
            Gen.Int[0, 100].Array[0, 4],
            Gen.Int[0, 100].Array[0, 4],
            static (ops, cut, suffixA, suffixB) => BuildCase(ops, cut, suffixA, suffixB));


    private static (OffsetAnchoredSequence<int> A, OffsetAnchoredSequence<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint) BuildCase((int Replica, int Seed)[] ops, int cut, int[] suffixA, int[] suffixB)
    {
        //Shared prefix on the common base: one sequence modelling common knowledge, with the dot each
        //insert minted recorded in op order (a remove op records no dot, marked by a null sentinel).
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(SharedBase);
        var targets = new List<OffsetAnchor>
        {
            OffsetAnchor.Head,
            OffsetAnchor.AtBase(0),
            OffsetAnchor.AtBase(1),
            OffsetAnchor.AtBase(2)
        };
        var dotsByOp = new List<Dot?>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = shared.VisibleElements.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                OffsetAnchor removeTarget = shared.VisibleElements[seed % visibleCount].Anchor;
                shared = shared.Remove(removeTarget);
                dotsByOp.Add(null);

                continue;
            }

            OffsetAnchor inserted;
            (shared, inserted) = shared.InsertAfter(targets[seed % targets.Count], (100 * replica) + opIndex, Replicas[replica]);
            targets.Add(inserted);
            dotsByOp.Add(inserted.LiveId);
        }

        //Frontier: a cut over the op count; the per-replica maximum counter over the dots the first cut
        //ops inserted. Ops past the cut stay shared but above the frontier — retained shared vertices.
        int boundedCut = Math.Min(cut, dotsByOp.Count);
        VectorClock frontier = VectorClock.Empty;
        for(int opIndex = 0; opIndex < boundedCut; opIndex++)
        {
            if(dotsByOp[opIndex] is { } dot)
            {
                frontier = RaiseTo(frontier, dot);
            }
        }

        //Checkpoint: from the final shared sequence, base entries plus stable live entries, in order.
        ImmutableArray<int>.Builder checkpointBuilder = ImmutableArray.CreateBuilder<int>();
        foreach((OffsetAnchor anchor, int value) in shared.VisibleElements)
        {
            if(anchor.LiveId is not { } liveId || IsStable(frontier, liveId))
            {
                checkpointBuilder.Add(value);
            }
        }

        ImmutableArray<int> checkpoint = checkpointBuilder.ToImmutable();

        //Insert-only divergent suffixes: removals above the frontier on stable elements would make A and
        //B derive different checkpoints, which the composition forbids, so the generator only inserts.
        OffsetAnchoredSequence<int> a = ApplySuffix(shared, Replicas[0], suffixA, 1000);
        OffsetAnchoredSequence<int> b = ApplySuffix(shared, Replicas[1], suffixB, 2000);

        return (a, b, frontier, checkpoint);
    }


    private static OffsetAnchoredSequence<int> ApplySuffix(OffsetAnchoredSequence<int> start, ReplicaId replica, int[] seeds, int valueBase)
    {
        OffsetAnchoredSequence<int> sequence = start;
        var targets = new List<OffsetAnchor>();
        foreach((OffsetAnchor anchor, int _) in sequence.VisibleElements)
        {
            targets.Add(anchor);
        }

        int value = valueBase;
        foreach(int seed in seeds)
        {
            OffsetAnchor anchor = targets.Count == 0 ? OffsetAnchor.Head : targets[seed % targets.Count];
            OffsetAnchor inserted;
            (sequence, inserted) = sequence.InsertAfter(anchor, value, replica);
            targets.Add(inserted);
            value++;
        }

        return sequence;
    }


    private static bool IsStable(VectorClock frontier, Dot dot)
    {
        return frontier[dot.Replica] >= dot.Counter;
    }


    private static VectorClock RaiseTo(VectorClock frontier, Dot dot)
    {
        VectorClock raised = frontier;
        while(raised[dot.Replica] < dot.Counter)
        {
            raised = raised.Increment(dot.Replica);
        }

        return raised;
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
