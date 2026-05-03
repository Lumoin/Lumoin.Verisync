using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class CheckpointedSequenceTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasEmptyLiveAndNoCheckpoint()
    {
        Assert.HasCount(0, CheckpointedSequence<string>.Empty.Live.Values);
        Assert.HasCount(0, CheckpointedSequence<string>.Empty.Checkpoint);
        Assert.IsNull(CheckpointedSequence<string>.Empty.CheckpointBallot);
    }


    [TestMethod]
    public void EditsAccumulateInLive()
    {
        (CheckpointedSequence<string> withA, Dot idA) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);
        (CheckpointedSequence<string> withB, _) = withA.InsertAfter(idA, "B", R1);

        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, withB.Live.Values.ToArray());
        Assert.HasCount(0, withB.Checkpoint);
    }


    [TestMethod]
    public void RemoveDeletesFromLive()
    {
        (CheckpointedSequence<string> withA, Dot idA) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);

        CheckpointedSequence<string> removed = withA.Remove(idA);

        Assert.HasCount(0, removed.Live.Values);
    }


    [TestMethod]
    public void PromoteCommitsLiveSnapshot()
    {
        (CheckpointedSequence<string> withA, Dot idA) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);
        (CheckpointedSequence<string> withB, _) = withA.InsertAfter(idA, "B", R1);
        CasPaxosRegister<ImmutableArray<string>> register = CasPaxosRegister<ImmutableArray<string>>.WithAcceptors(3);

        (CheckpointedSequence<string> promoted, _, ChangeOutcome<ImmutableArray<string>> outcome) = withB.Promote(register, new Ballot(1, R1));

        Assert.IsTrue(outcome.IsChosen);
        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, promoted.Checkpoint.ToArray());
        Assert.AreEqual(new Ballot(1, R1), promoted.CheckpointBallot);
    }


    [TestMethod]
    public void EditsAfterCheckpointStayInLive()
    {
        (CheckpointedSequence<string> withA, Dot idA) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);
        (CheckpointedSequence<string> withB, Dot idB) = withA.InsertAfter(idA, "B", R1);
        CasPaxosRegister<ImmutableArray<string>> register = CasPaxosRegister<ImmutableArray<string>>.WithAcceptors(3);
        (CheckpointedSequence<string> promoted, _, _) = withB.Promote(register, new Ballot(1, R1));

        (CheckpointedSequence<string> edited, _) = promoted.InsertAfter(idB, "C", R1);

        string[] liveExpected = ["A", "B", "C"];
        string[] checkpointExpected = ["A", "B"];
        CollectionAssert.AreEqual(liveExpected, edited.Live.Values.ToArray());
        CollectionAssert.AreEqual(checkpointExpected, edited.Checkpoint.ToArray());
    }


    [TestMethod]
    public void MergeConvergesLiveAcrossReplicas()
    {
        (CheckpointedSequence<string> a, _) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);
        (CheckpointedSequence<string> b, _) = CheckpointedSequence<string>.Empty.InsertAtHead("B", R2);

        CheckpointedSequence<string> merged = a.Merge(b);

        Assert.HasCount(2, merged.Live.Values);
        Assert.Contains("A", merged.Live.Values);
        Assert.Contains("B", merged.Live.Values);
    }


    [TestMethod]
    public void MergeKeepsLaterCheckpoint()
    {
        CasPaxosRegister<ImmutableArray<string>> register = CasPaxosRegister<ImmutableArray<string>>.WithAcceptors(3);
        (CheckpointedSequence<string> a, _) = CheckpointedSequence<string>.Empty.InsertAtHead("A", R1);
        (CheckpointedSequence<string> earlier, CasPaxosRegister<ImmutableArray<string>> register1, _) = a.Promote(register, new Ballot(1, R1));
        (CheckpointedSequence<string> b, _) = CheckpointedSequence<string>.Empty.InsertAtHead("B", R2);
        (CheckpointedSequence<string> later, _, _) = b.Promote(register1, new Ballot(2, R1));

        CheckpointedSequence<string> merged = earlier.Merge(later);

        Assert.AreEqual(new Ballot(2, R1), merged.CheckpointBallot);
        string[] expected = ["B"];
        CollectionAssert.AreEqual(expected, merged.Checkpoint.ToArray());
    }


    [TestMethod]
    public void MergeRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<string>.Empty.Merge(null!));
    }


    [TestMethod]
    public void PromoteRejectsNullRegister()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<string>.Empty.Promote(null!, new Ballot(1, R1)));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
