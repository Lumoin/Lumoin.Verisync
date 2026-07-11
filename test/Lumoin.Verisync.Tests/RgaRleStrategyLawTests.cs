using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.RgaRleV2"/> strategy with the shared law
/// harness, exercising the four compaction laws on top of the join-semilattice and intention laws. The
/// generated operands share one multi-replica prefix built on an empty <see cref="Rga{TValue}"/> and
/// diverge by suffixes that both insert and remove: under the certified projection an above-frontier
/// divergent remove is simply not yet certified, so both operands derive the SAME checkpoint at the
/// shared frontier — the frontier is the shared-prefix state's context at the cut, and the checkpoint is
/// that prefix state's visible values.
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


    protected override Gen<(Rga<int> Sequence, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)>? GenCompactionCase
    {
        get
        {
            return GenCase.Select(static input => (input.A.Merge(input.B), input.Frontier, input.Checkpoint));
        }
    }


    protected override Gen<(Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)>? GenCommutationCase => GenCase;


    //The shared-prefix ops, the frontier cut, and the two divergent insert-and-remove suffixes all come from
    //CsCheck seeds, so the whole case is deterministic and reproduces on shrink. Filtered so every sampled
    //case actually drops a vertex when the merged operands compact at the shared frontier — unfiltered random
    //histories reach the drop region only a few percent of the time, leaving the compaction laws asserting
    //on no-op compactions.
    private static Gen<(Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint)> GenCase { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 6],
            Gen.Int[0, 6],
            Gen.Int[0, 100].Array[0, 4],
            Gen.Int[0, 100].Array[0, 4],
            static (ops, cut, suffixA, suffixB) => BuildCase(ops, cut, suffixA, suffixB))
        .Where(static input => MergedCompactionDrops(input.A, input.B, input.Frontier, input.Checkpoint));


    private static (Rga<int> A, Rga<int> B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<int>> Checkpoint) BuildCase((int Replica, int Seed)[] ops, int cut, int[] suffixA, int[] suffixB)
    {
        //Shared prefix on an empty Rga: the common knowledge every derived operand starts from. The state at
        //the cut is snapshotted to supply the frontier and the certified checkpoint. Removes are dotted, so
        //the cut context covers the prefix's remove-dots as well as its insert-dots.
        int boundedCut = Math.Min(cut, ops.Length);
        Rga<int> shared = Rga<int>.Empty;
        var insertedDots = new List<Dot>();
        Rga<int> prefixAtCut = Rga<int>.Empty;
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = shared.Values.Count;
            if(seed % 3 == 0 && visibleCount > 0)
            {
                //Biased to the most recently inserted still-visible element: a recent insert is most likely
                //childless, the only kind of tombstone the drop gate can fold, so cases actually reach it.
                shared = shared.Remove(MostRecentVisibleDot(shared, insertedDots), Replicas[replica]);
            }
            else
            {
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
            }

            if(opIndex + 1 == boundedCut)
            {
                prefixAtCut = shared;
            }
        }

        //Frontier: the prefix state's context at the cut. Counters advance monotonically over the sequential
        //prefix, so this cleanly covers every dot minted at or below the cut and none minted after it — an
        //honest "every member observed exactly the prefix" frontier.
        VectorClock frontier = prefixAtCut.CausalContext;

        //Checkpoint: the prefix state's DOTTED certified projection at the cut. This IS the certified
        //projection of every derived operand at that frontier — an element is certified-projected iff it was
        //inserted and not yet removed within the cut, exactly the cut state's visible content, and RGA order
        //is superset-stable, so the shared prefix's dots and values carry across every operand unchanged.
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = prefixAtCut.CertifiedProjection(frontier);

        //Divergent suffixes now insert AND remove: an above-frontier divergent remove is not yet certified, so
        //both operands still derive the same checkpoint at the shared frontier.
        Rga<int> a = ApplySuffix(shared, Replicas[0], suffixA, 1000);
        Rga<int> b = ApplySuffix(shared, Replicas[1], suffixB, 2000);

        return (a, b, frontier, checkpoint);
    }


    private static Rga<int> ApplySuffix(Rga<int> start, ReplicaId replica, int[] seeds, int valueBase)
    {
        Rga<int> sequence = start;
        int next = valueBase;
        foreach(int seed in seeds)
        {
            int visibleCount = sequence.Values.Count;
            if(seed % 4 == 0 && visibleCount > 0)
            {
                //A divergent above-frontier remove on a visible element, mirroring the prefix style.
                int visibleValue = sequence.Values[seed % visibleCount];
                sequence = sequence.Remove(DotOfValue(sequence, visibleValue), replica);

                continue;
            }

            if(visibleCount == 0)
            {
                (sequence, _) = sequence.InsertAtHead(next, replica);
            }
            else
            {
                Dot target = DotOfValue(sequence, sequence.Values[seed % visibleCount]);
                (sequence, _) = sequence.InsertAfter(target, next, replica);
            }

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


    private static Dot MostRecentVisibleDot(Rga<int> sequence, List<Dot> insertedDots)
    {
        var hidden = new HashSet<Dot>();
        foreach(RgaTombstoneEntry tombstone in sequence.ToState().Tombstones)
        {
            hidden.Add(new Dot(ReplicaId.FromSpan(tombstone.Target.Replica.AsSpan()), tombstone.Target.Counter));
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


    private static bool MergedCompactionDrops(Rga<int> a, Rga<int> b, VectorClock frontier, ImmutableArray<SequenceCheckpointEntry<int>> checkpoint)
    {
        Rga<int> merged = a.Merge(b);
        Rga<int> compacted = merged.Compact(frontier, checkpoint);
        foreach(RgaVertexEntry<int> vertex in merged.ToState().Vertices)
        {
            var dot = new Dot(ReplicaId.FromSpan(vertex.Id.Replica.AsSpan()), vertex.Id.Counter);
            Dot? translated = compacted.TranslateAnchor(dot);
            if(translated is not null && !translated.Equals(dot))
            {
                return true;
            }
        }

        return false;
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
