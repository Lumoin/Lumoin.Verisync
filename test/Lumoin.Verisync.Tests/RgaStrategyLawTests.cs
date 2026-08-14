using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Registers the <see cref="WellKnownSequenceStrategies.RgaV2"/> strategy with the shared law harness.
/// The laws are exercised through the context delegates — the same surface
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> uses — complementing the type-level
/// property tests in <see cref="RgaPropertyTests"/>.
/// </summary>
[TestClass]
internal sealed class RgaStrategyLawTests: SequenceStrategyLawTests<Rga<int>, int, Dot>
{
    private static ReplicaId[] Replicas { get; } = [MakeReplica(0), MakeReplica(1), MakeReplica(2), MakeReplica(3)];


    protected override SequenceCrdtContext<Rga<int>, int, Dot> Context { get; } = WellKnownSequenceStrategies.CreateRga<int>();


    /// <summary>
    /// The sentinel sits outside every generator's value range (replica * 1000 + ordinal, all non-negative).
    /// </summary>
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


    /// <summary>
    /// Plain RGA runs LAW-NFD for uniformity: an op history of inserts and dotted removes over an empty Rga
    /// with a strict prefix cut.
    /// </summary>
    /// <remarks>
    /// Its semantic coverage coincides with the rga-rle registration — both wire the identical
    /// Rga&lt;int&gt;.Merge — so it is uniformity, not new merge coverage.
    /// </remarks>
    protected override Gen<(Rga<int> Full, Rga<int> Behind)> GenFullAndBehindHistory { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 8],
            Gen.Int[0, 8],
            static (ops, cut) =>
            {
                (Rga<int> full, IReadOnlyList<Rga<int>> snapshots) = BuildSnapshots(ops);

                return (Full: full, Behind: SnapshotAt(snapshots, cut));
            }).Where(static pair => !pair.Full.Equals(pair.Behind));


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
                sequence = sequence.Remove(MostRecentVisibleDot(sequence, insertedDots), Replicas[replica]);
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
