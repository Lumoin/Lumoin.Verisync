using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.RgaV1"/> strategy with the shared law harness.
/// The laws are exercised through the context delegates — the same surface
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> uses — complementing the type-level
/// property tests in <see cref="RgaPropertyTests"/>.
/// </summary>
[TestClass]
internal sealed class RgaStrategyLawTests: SequenceStrategyLawTests<Rga<int>, int, Dot>
{
    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2), MakeReplica(3)];


    protected override SequenceCrdtContext<Rga<int>, int, Dot> Context { get; } = WellKnownSequenceStrategies.CreateRga<int>();


    //The sentinel sits outside every generator's value range (replica * 1000 + ordinal, all non-negative).
    protected override int FreshValue => -1;


    protected override Gen<Rga<int>> GenFromReplica(int replicaIndex)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => BuildChain(replicaIndex, seeds));
    }


    protected override ReplicaId Replica(int replicaIndex) => Replicas[replicaIndex];


    protected override Dot AnchorOfVisibleElement(Rga<int> sequence, int index)
    {
        //Values are globally unique by construction (per-replica ranges), so the anchor of the visible
        //element is recoverable from the serialized vertices by value.
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
