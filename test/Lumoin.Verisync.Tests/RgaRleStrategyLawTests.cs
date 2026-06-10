using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.RgaRleV1"/> strategy with the shared law
/// harness, exercising the four compaction laws on top of the join-semilattice and intention laws. The
/// generated operands share one multi-replica prefix built on an empty <see cref="Rga{TValue}"/> and
/// diverge only by insert-only suffixes — a same-generation merge for which <see cref="Rga{TValue}"/>
/// has no generation gate, so the compact/merge commutation law holds without alignment caveats.
/// </summary>
[TestClass]
internal sealed class RgaRleStrategyLawTests: SequenceStrategyLawTests<Rga<int>, int, Dot>
{
    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2), MakeReplica(3)];


    protected override SequenceCrdtContext<Rga<int>, int, Dot> Context { get; } = WellKnownSequenceStrategies.CreateRgaRle<int>();


    //The sentinel sits outside every generator's value range (per-replica prefix and suffix ranges, all non-negative).
    protected override int FreshValue => -1;


    protected override Gen<Rga<int>> GenFromReplica(int replicaIndex)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => BuildChain(replicaIndex, seeds));
    }


    protected override ReplicaId Replica(int replicaIndex) => Replicas[replicaIndex];


    protected override Dot AnchorOfVisibleElement(Rga<int> sequence, int index)
    {
        //Values are globally unique by construction (per-replica prefix and suffix ranges), so the anchor
        //of the visible element is recoverable from the serialized vertices by value.
        int value = sequence.Values[index];
        foreach(RgaVertexEntry<int> entry in sequence.ToState().Vertices)
        {
            if(entry.Value == value)
            {
                return new Dot(ReplicaId.FromSpan(entry.Id.Replica.AsSpan()), entry.Id.Counter);
            }
        }

        throw new InvalidOperationException("The visible element was not found.");
    }


    protected override Gen<(Rga<int> Sequence, VectorClock Frontier, ImmutableArray<int> Checkpoint)>? GenCompactionCase
    {
        get
        {
            return GenCase.Select(static input => (input.A.Merge(input.B), input.Frontier, input.Checkpoint));
        }
    }


    protected override Gen<(Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint)>? GenCommutationCase => GenCase;


    //The shared-prefix ops, the frontier cut, and the two insert-only divergent suffixes all come from
    //CsCheck seeds, so the whole case is deterministic and reproduces on shrink.
    private static Gen<(Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint)> GenCase { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 6],
            Gen.Int[0, 6],
            Gen.Int[0, 100].Array[0, 4],
            Gen.Int[0, 100].Array[0, 4],
            static (ops, cut, suffixA, suffixB) => BuildCase(ops, cut, suffixA, suffixB));


    private static (Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<int> Checkpoint) BuildCase((int Replica, int Seed)[] ops, int cut, int[] suffixA, int[] suffixB)
    {
        //Shared prefix on an empty Rga: one sequence modelling common knowledge, with the dot each insert
        //minted recorded in op order (a remove op records no dot, marked by a null sentinel).
        Rga<int> shared = Rga<int>.Empty;
        var insertedDots = new List<Dot>();
        var dotsByOp = new List<Dot?>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = shared.Values.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                int visibleValue = shared.Values[seed % visibleCount];
                Dot removeTarget = DotOfValue(shared, visibleValue);
                shared = shared.Remove(removeTarget);
                dotsByOp.Add(null);

                continue;
            }

            Dot inserted;
            if(insertedDots.Count == 0)
            {
                (shared, inserted) = shared.InsertAtHead((100 * replica) + opIndex, Replicas[replica]);
            }
            else
            {
                (shared, inserted) = shared.InsertAfter(insertedDots[seed % insertedDots.Count], (100 * replica) + opIndex, Replicas[replica]);
            }

            insertedDots.Add(inserted);
            dotsByOp.Add(inserted);
        }

        //Frontier: a cut over the op count; the per-replica maximum counter over the dots the first cut ops
        //inserted. Counters are Lamport-monotone over the sequential prefix, so this is a clean prefix frontier.
        int boundedCut = Math.Min(cut, dotsByOp.Count);
        VectorClock frontier = VectorClock.Empty;
        for(int opIndex = 0; opIndex < boundedCut; opIndex++)
        {
            if(dotsByOp[opIndex] is { } dot)
            {
                frontier = RaiseTo(frontier, dot);
            }
        }

        //Checkpoint: the shared sequence's visible values filtered to stable dots, in order.
        ImmutableArray<int>.Builder checkpointBuilder = ImmutableArray.CreateBuilder<int>();
        foreach(int value in shared.Values)
        {
            Dot dot = DotOfValue(shared, value);
            if(IsStable(frontier, dot))
            {
                checkpointBuilder.Add(value);
            }
        }

        ImmutableArray<int> checkpoint = checkpointBuilder.ToImmutable();

        //Insert-only divergent suffixes: removals above the frontier on stable elements would make A and B
        //derive different checkpoints, which the composition forbids, so the generator only inserts.
        Rga<int> a = ApplySuffix(shared, Replicas[0], suffixA, 1000);
        Rga<int> b = ApplySuffix(shared, Replicas[1], suffixB, 2000);

        return (a, b, frontier, checkpoint);
    }


    private static Rga<int> ApplySuffix(Rga<int> start, ReplicaId replica, int[] seeds, int valueBase)
    {
        Rga<int> sequence = start;
        var targets = new List<Dot>();
        foreach(int value in sequence.Values)
        {
            targets.Add(DotOfValue(sequence, value));
        }

        int next = valueBase;
        foreach(int seed in seeds)
        {
            Dot inserted;
            if(targets.Count == 0)
            {
                (sequence, inserted) = sequence.InsertAtHead(next, replica);
            }
            else
            {
                (sequence, inserted) = sequence.InsertAfter(targets[seed % targets.Count], next, replica);
            }

            targets.Add(inserted);
            next++;
        }

        return sequence;
    }


    private static Dot DotOfValue(Rga<int> sequence, int value)
    {
        foreach(RgaVertexEntry<int> entry in sequence.ToState().Vertices)
        {
            if(entry.Value == value)
            {
                return new Dot(ReplicaId.FromSpan(entry.Id.Replica.AsSpan()), entry.Id.Counter);
            }
        }

        throw new InvalidOperationException("The value was not found among the vertices.");
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


    private static Rga<int> BuildChain(int replicaIndex, int[] seeds)
    {
        Rga<int> rga = Rga<int>.Empty;
        var ids = new List<Dot>();
        int value = replicaIndex * 1000;

        foreach(int seed in seeds)
        {
            Dot inserted;
            if(ids.Count == 0)
            {
                (rga, inserted) = rga.InsertAtHead(value, Replicas[replicaIndex]);
            }
            else
            {
                (rga, inserted) = rga.InsertAfter(ids[seed % ids.Count], value, Replicas[replicaIndex]);
            }

            ids.Add(inserted);
            value++;
        }

        return rga;
    }


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
