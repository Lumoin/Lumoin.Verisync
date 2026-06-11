using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class CheckpointedSequenceTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void CreateGivesEmptyLiveAndNoCheckpoint()
    {
        CheckpointedSequence<Rga<string>, string, Dot> sequence = NewSequence();

        Assert.HasCount(0, sequence.Values);
        Assert.HasCount(0, sequence.Checkpoint);
        Assert.IsNull(sequence.CheckpointBallot);
        Assert.AreEqual(WellKnownSequenceStrategies.RgaV1, sequence.StrategyId);
    }


    [TestMethod]
    public void CreateRejectsNullArguments()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> context = WellKnownSequenceStrategies.CreateRga<string>();

        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Create(null!, Canonicalize, Sha256));
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Create(context, null!, Sha256));
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Create(context, Canonicalize, null!));
    }


    [TestMethod]
    public void TheRgaStrategyIdentifierIsPinned()
    {
        //The identifier is part of the replication contract: changing it is a protocol break, so it is
        //pinned literally here, not referenced through the constant it must equal.
        Assert.AreEqual("verisync.sequence.rga.v1", WellKnownSequenceStrategies.CreateRga<string>().StrategyId);
    }


    [TestMethod]
    public void EditsAccumulateInLive()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = withA.InsertAfter(idA, "B", R1);

        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, withB.Values.ToArray());
        Assert.HasCount(0, withB.Checkpoint);
    }


    [TestMethod]
    public void RemoveDeletesFromLive()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = NewSequence().InsertAtHead("A", R1);

        CheckpointedSequence<Rga<string>, string, Dot> removed = withA.Remove(idA);

        Assert.HasCount(0, removed.Values);
    }


    [TestMethod]
    public void PromoteAgreesOnTheCommitmentAndKeepsContentLocal()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = withA.InsertAfter(idA, "B", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        (CheckpointedSequence<Rga<string>, string, Dot> promoted, _, ChangeOutcome<CheckpointCommitment> outcome) = withB.Promote(register, new Ballot(1, R1));

        Assert.IsTrue(outcome.IsChosen);
        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, promoted.Checkpoint.ToArray());
        Assert.AreEqual(new Ballot(1, R1), promoted.CheckpointBallot);

        //The register carries the digest of the snapshot's canonical bytes - metadata-sized - never the
        //snapshot itself; the local commitment matches an independent recomputation.
        byte[] recomputed = SHA256.HashData(Canonicalize([.. expected]).Span);
        Assert.AreEqual(new CheckpointCommitment(recomputed), outcome.Value);
        Assert.AreEqual(new CheckpointCommitment(recomputed), promoted.Commitment);
        Assert.AreEqual(32, outcome.Value!.Digest.Length);
    }


    [TestMethod]
    public void EditsAfterCheckpointStayInLive()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "B", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> promoted, _, _) = withB.Promote(register, new Ballot(1, R1));

        (CheckpointedSequence<Rga<string>, string, Dot> edited, _) = promoted.InsertAfter(idB, "C", R1);

        string[] liveExpected = ["A", "B", "C"];
        string[] checkpointExpected = ["A", "B"];
        CollectionAssert.AreEqual(liveExpected, edited.Values.ToArray());
        CollectionAssert.AreEqual(checkpointExpected, edited.Checkpoint.ToArray());
    }


    [TestMethod]
    public void MergeConvergesLiveAcrossReplicas()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> a, _) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> b, _) = NewSequence().InsertAtHead("B", R2);

        CheckpointedSequence<Rga<string>, string, Dot> merged = a.Merge(b);

        Assert.HasCount(2, merged.Values);
        Assert.Contains("A", merged.Values);
        Assert.Contains("B", merged.Values);
    }


    [TestMethod]
    public void MergeKeepsLaterCheckpoint()
    {
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> a, _) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> earlier, CasPaxosRegister<CheckpointCommitment> register1, _) = a.Promote(register, new Ballot(1, R1));
        (CheckpointedSequence<Rga<string>, string, Dot> b, _) = NewSequence().InsertAtHead("B", R2);
        (CheckpointedSequence<Rga<string>, string, Dot> later, _, _) = b.Promote(register1, new Ballot(2, R1));

        CheckpointedSequence<Rga<string>, string, Dot> merged = earlier.Merge(later);

        Assert.AreEqual(new Ballot(2, R1), merged.CheckpointBallot);
        string[] expected = ["B"];
        CollectionAssert.AreEqual(expected, merged.Checkpoint.ToArray());
        Assert.AreEqual(later.Commitment, merged.Commitment);
    }


    [TestMethod]
    public void MergingDifferentStrategiesFailsClosed()
    {
        //The strategy is part of the replication contract: replicas running different strategies do not
        //degrade, they silently diverge, so the mismatch must throw rather than merge.
        SequenceCrdtContext<Rga<string>, string, Dot> variant = WellKnownSequenceStrategies.CreateRga<string>();
        CheckpointedSequence<Rga<string>, string, Dot> standard = NewSequence();
        CheckpointedSequence<Rga<string>, string, Dot> renamed = CheckpointedSequence<Rga<string>, string, Dot>.Create(new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = "verisync.sequence.rga.v2-experimental",
            Empty = variant.Empty,
            InsertAtHead = variant.InsertAtHead,
            InsertAfter = variant.InsertAfter,
            Remove = variant.Remove,
            Merge = variant.Merge,
            Values = variant.Values
        }, Canonicalize, Sha256);

        Assert.ThrowsExactly<InvalidOperationException>(() => standard.Merge(renamed));
    }


    [TestMethod]
    public void MergeRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => NewSequence().Merge(null!));
    }


    [TestMethod]
    public void PromoteRejectsNullRegister()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => NewSequence().Promote(null!, new Ballot(1, R1)));
    }


    private static CheckpointedSequence<Rga<string>, string, Dot> NewSequence()
    {
        return CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRga<string>(), Canonicalize, Sha256);
    }


    private static ReadOnlyMemory<byte> Canonicalize(ImmutableArray<string> values)
    {
        //Unit separator between elements keeps the encoding unambiguous for these test values.
        return Encoding.UTF8.GetBytes(string.Join('\u001F', values));
    }


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
