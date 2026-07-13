using Lumoin.Verisync.Core;
using System.Collections.Immutable;
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
        Assert.AreEqual(WellKnownSequenceStrategies.RgaV2, sequence.StrategyId);
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
        Assert.AreEqual("verisync.sequence.rga.v2", WellKnownSequenceStrategies.CreateRga<string>().StrategyId);
    }


    [TestMethod]
    public void TheOffsetStrategyIdentifierIsPinned()
    {
        //offset.v2 certifies both removal kinds; the v1 identifier's semantics no longer exist in code,
        //and published identifiers never change meaning.
        Assert.AreEqual("verisync.sequence.offset.v2", WellKnownSequenceStrategies.CreateOffset<string>().StrategyId);
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

        CheckpointedSequence<Rga<string>, string, Dot> removed = withA.Remove(idA, R1);

        Assert.HasCount(0, removed.Values);
    }


    [TestMethod]
    public void CausalContextIsTheLiveClockForRgaAndNullWithoutTheDelegate()
    {
        //The rga strategy wires the causal-context accessor, so the container advertises the live sequence's
        //clock; after one head insert on R1 the clock reads one on R1's axis.
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = NewSequence().InsertAtHead("A", R1);
        Assert.IsNotNull(withA.CausalContext);
        Assert.AreEqual(1, withA.CausalContext![R1]);

        //offset.v2 advertises a live causal context too, and its dotted removes tick it through the
        //container wiring: one insert plus one remove reads two on R1's axis.
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> offset =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Create(
                WellKnownSequenceStrategies.CreateOffset<string>(), Canonicalize, Sha256);
        (CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> withOffsetA, OffsetAddress offsetAnchor) = offset.InsertAtHead("A", R1);
        Assert.IsNotNull(withOffsetA.CausalContext);
        Assert.AreEqual(1, withOffsetA.CausalContext![R1]);
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> offsetRemoved = withOffsetA.Remove(offsetAnchor, R1);
        Assert.AreEqual(2, offsetRemoved.CausalContext![R1]);

        //A context built WITHOUT the delegate advertises none.
        SequenceCrdtContext<OffsetAnchoredSequence<string>, string, OffsetAddress> wired = WellKnownSequenceStrategies.CreateOffset<string>();
        CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress> bare =
            CheckpointedSequence<OffsetAnchoredSequence<string>, string, OffsetAddress>.Create(new SequenceCrdtContext<OffsetAnchoredSequence<string>, string, OffsetAddress>
            {
                StrategyId = wired.StrategyId,
                Empty = wired.Empty,
                InsertAtHead = wired.InsertAtHead,
                InsertAfter = wired.InsertAfter,
                Remove = wired.Remove,
                Merge = wired.Merge,
                Values = wired.Values
            }, Canonicalize, Sha256);
        Assert.IsNull(bare.CausalContext);
    }


    //A sealed checkpoint's content stays in the compactable strategy's live sequence; edits after the seal
    //accumulate live while the recorded checkpoint holds the sealed dotted content.
    [TestMethod]
    public void EditsAfterCheckpointStayInLive()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, Dot idB) = withA.InsertAfter(idA, "B", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> afterSeal, _, _, _) = withB.Seal(register, new Ballot(1, R1), withB.CausalContext!);

        (CheckpointedSequence<Rga<string>, string, Dot> edited, _) = afterSeal.InsertAfter(idB, "C", R1);

        string[] liveExpected = ["A", "B", "C"];
        string[] checkpointExpected = ["A", "B"];
        CollectionAssert.AreEqual(liveExpected, edited.Values.ToArray());
        CollectionAssert.AreEqual(checkpointExpected, CheckpointValues(edited.Checkpoint));
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


    //Two seals on an ascending frontier chain leave the later checkpoint recorded at the higher ballot;
    //merging the earlier container with the later one keeps that later checkpoint.
    [TestMethod]
    public void MergeKeepsLaterCheckpoint()
    {
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> earlier, CasPaxosRegister<CheckpointCommitment> register1, _, _) = withA.Seal(register, new Ballot(1, R1), withA.CausalContext!);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = earlier.InsertAfter(idA, "B", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> later, _, _, _) = withB.Seal(register1, new Ballot(2, R1), withB.CausalContext!);

        CheckpointedSequence<Rga<string>, string, Dot> merged = earlier.Merge(later);

        Assert.AreEqual(new Ballot(2, R1), merged.CheckpointBallot);
        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, CheckpointValues(merged.Checkpoint));
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
    public void SealRejectsNullRegister()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Sealable().Seal(null!, new Ballot(1, R1), VectorClock.Empty));
    }


    //The container's probe checks exist independently of the strategy guard: RGA's Compact never
    //imposes an insert-quiescence precondition, so with a hand-built context whose probe constantly
    //reports one unstable insert, any quiescence throw below can only come from the container itself —
    //both from Seal and from ApplyCommittedSeal with an honestly-built commitment whose dominance,
    //chain, and digest checks all pass.
    [TestMethod]
    public void TheContainerRefusesToSealWhenTheProbeReportsInstability()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var probed = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = wired.Compact,
            TranslateAnchor = wired.TranslateAnchor,
            CausalContext = wired.CausalContext,
            CertifyProjection = wired.CertifyProjection,
            UnstableInserts = static (_, _) => [new Dot(R1, 99)]
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(probed, Canonicalize, Sha256).InsertAtHead("A", R1);
        VectorClock frontier = withA.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        Assert.ThrowsExactly<InvalidOperationException>(() => withA.Seal(register, new Ballot(1, R1), frontier));

        //The probe-refused Seal ran no consensus round, so the register is untouched: a probe-LESS context
        //over the same delegates seals it at the SAME frontier as the register's first commitment.
        var probeless = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = wired.Compact,
            TranslateAnchor = wired.TranslateAnchor,
            CausalContext = wired.CausalContext,
            CertifyProjection = wired.CertifyProjection
        };
        (CheckpointedSequence<Rga<string>, string, Dot> probelessA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(probeless, Canonicalize, Sha256).InsertAtHead("A", R1);
        (_, _, ChangeOutcome<CheckpointCommitment> probelessOutcome, bool probelessSealed) =
            probelessA.Seal(register, new Ballot(1, R1), frontier);
        Assert.IsTrue(probelessSealed);
        Assert.AreEqual(frontier, probelessOutcome.Value!.Frontier);

        var committed = new CheckpointCommitment(frontier, Sha256(Canonicalize(withA.Live.CertifiedProjection(frontier))));
        Assert.ThrowsExactly<InvalidOperationException>(() => withA.ApplyCommittedSeal(committed, new Ballot(1, R1)));
    }


    //The digest check runs BEFORE the probe in ApplyCommittedSeal: with the probe wired to throw a
    //marker exception and a MISMATCHED digest, the digest-first order surfaces the digest's
    //InvalidOperationException; a probe-first implementation would surface NotSupportedException. The
    //ordering is pinned by exception TYPE alone.
    [TestMethod]
    public void TheDigestCheckPrecedesTheProbeInApplyCommittedSeal()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var probed = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = wired.Compact,
            TranslateAnchor = wired.TranslateAnchor,
            CausalContext = wired.CausalContext,
            CertifyProjection = wired.CertifyProjection,
            UnstableInserts = static (_, _) => throw new NotSupportedException()
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(probed, Canonicalize, Sha256).InsertAtHead("A", R1);
        VectorClock frontier = withA.CausalContext!;

        ReadOnlyMemory<byte> mismatched = new byte[] { 1, 2, 3 };
        var committed = new CheckpointCommitment(frontier, mismatched);

        Assert.ThrowsExactly<InvalidOperationException>(() => withA.ApplyCommittedSeal(committed, new Ballot(1, R1)));
    }


    private static CheckpointedSequence<Rga<string>, string, Dot> NewSequence()
    {
        return CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRga<string>(), Canonicalize, Sha256);
    }


    private static CheckpointedSequence<Rga<string>, string, Dot> Sealable()
    {
        return CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRgaRle<string>(), Canonicalize, Sha256);
    }


    private static string[] CheckpointValues(ImmutableArray<SequenceCheckpointEntry<string>> checkpoint)
    {
        var values = new string[checkpoint.Length];
        for(int i = 0; i < checkpoint.Length; i++)
        {
            values[i] = checkpoint[i].Value;
        }

        return values;
    }


    //Encodes each dotted entry deterministically as dot replica hex, counter, and value, so equal checkpoints
    //produce equal canonical bytes on every replica.
    private static ReadOnlyMemory<byte> Canonicalize(ImmutableArray<SequenceCheckpointEntry<string>> entries)
    {
        var builder = new StringBuilder();
        foreach(SequenceCheckpointEntry<string> entry in entries)
        {
            builder.Append(Convert.ToHexStringLower(entry.Dot.Replica.AsSpan()));
            builder.Append(':');
            builder.Append(entry.Dot.Counter);
            builder.Append(':');
            builder.Append(entry.Value);
            builder.Append('\u001F');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
