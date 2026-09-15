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

        Assert.IsEmpty(sequence.Values);
        Assert.IsEmpty(sequence.Checkpoint);
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
        Assert.AreSequenceEqual(expected, withB.Values.ToArray());
        Assert.IsEmpty(withB.Checkpoint);
    }


    [TestMethod]
    public void RemoveDeletesFromLive()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = NewSequence().InsertAtHead("A", R1);

        CheckpointedSequence<Rga<string>, string, Dot> removed = withA.Remove(idA, R1);

        Assert.IsEmpty(removed.Values);
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

        //A context built without the delegate advertises none.
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


    /// <summary>
    /// A sealed checkpoint's content stays in the compactable strategy's live sequence; edits after the seal
    /// accumulate live while the recorded checkpoint holds the sealed dotted content.
    /// </summary>
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
        Assert.AreSequenceEqual(liveExpected, edited.Values.ToArray());
        Assert.AreSequenceEqual(checkpointExpected, CheckpointValues(edited.Checkpoint));
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


    /// <summary>
    /// Two seals on an ascending frontier chain leave the later checkpoint recorded at the higher ballot;
    /// merging the earlier container with the later one keeps that later checkpoint.
    /// </summary>
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
        Assert.AreSequenceEqual(expected, CheckpointValues(merged.Checkpoint));
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


    /// <summary>
    /// The container's probe checks exist independently of the strategy guard: RGA's Compact never
    /// imposes an insert-quiescence precondition, so with a hand-built context whose probe constantly
    /// reports one unstable insert, any quiescence throw below can only come from the container itself —
    /// both from Seal and from ApplyCommittedSeal with an honestly-built commitment whose dominance,
    /// chain, and digest checks all pass.
    /// </summary>
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

        //The probe-refused Seal ran no consensus round, so the register is untouched: a probe-less context
        //over the same delegates seals it at the same frontier as the register's first commitment.
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


    /// <summary>
    /// The digest check runs before the probe in ApplyCommittedSeal: with the probe wired to throw a
    /// marker exception and a mismatched digest, the digest-first order surfaces the digest's
    /// InvalidOperationException; a probe-first implementation would surface NotSupportedException.
    /// </summary>
    /// <remarks>
    /// The ordering is pinned by exception type alone.
    /// </remarks>
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


    /// <summary>
    /// Two containers with identical live content, checkpoint, and commitment but different checkpoint
    /// ballots are never equal.
    /// </summary>
    [TestMethod]
    public void EqualsRequiresMatchingCheckpointBallot()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = Sealable().InsertAtHead("a", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (_, _, ChangeOutcome<CheckpointCommitment> outcome, bool wasSealed) =
            withA.Seal(register, new Ballot(1, R1), withA.CausalContext!);
        Assert.IsTrue(wasSealed);

        CheckpointedSequence<Rga<string>, string, Dot> appliedAtBallot1 = withA.ApplyCommittedSeal(outcome.Value!, new Ballot(1, R1));
        CheckpointedSequence<Rga<string>, string, Dot> appliedAtBallot2 = withA.ApplyCommittedSeal(outcome.Value!, new Ballot(2, R1));

        Assert.IsFalse(appliedAtBallot1.Equals(appliedAtBallot2));
    }


    /// <summary>Returns a typed null so the strongly typed Equals overload is chosen over Equals(object).</summary>
    private static CheckpointedSequence<Rga<string>, string, Dot>? NullSequence() => null;


    /// <summary>
    /// Equals against a null other is always false, never a coincidental true.
    /// </summary>
    [TestMethod]
    public void EqualsReturnsFalseForNull()
    {
        CheckpointedSequence<Rga<string>, string, Dot> sequence = NewSequence();

        Assert.IsFalse(sequence.Equals(NullSequence()));
    }


    /// <summary>
    /// Seal's own ArgumentNullException.ThrowIfNull(stabilityFrontier) guard fires, and reports its own
    /// parameter name, independently of what a wired CertifyProjection delegate would do with a null frontier.
    /// </summary>
    [TestMethod]
    public void SealRejectsNullFrontierEvenWhenTheStrategyDelegateIgnoresIt()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRga<string>();
        var permissive = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = static (sequence, _, _) => sequence,
            CertifyProjection = static (_, _) => ImmutableArray<SequenceCheckpointEntry<string>>.Empty
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(permissive, Canonicalize, Sha256).InsertAtHead("A", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => withA.Seal(register, new Ballot(1, R1), null!));

        Assert.AreEqual("stabilityFrontier", thrown.ParamName);
    }


    /// <summary>
    /// Two otherwise-identical containers under different strategy identifiers are never equal: the strategy
    /// is part of the replication contract, not incidental state.
    /// </summary>
    [TestMethod]
    public void EqualsRequiresMatchingStrategyId()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> variant = WellKnownSequenceStrategies.CreateRga<string>();
        CheckpointedSequence<Rga<string>, string, Dot> standard = NewSequence();
        CheckpointedSequence<Rga<string>, string, Dot> renamed = CheckpointedSequence<Rga<string>, string, Dot>.Create(new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = "verisync.sequence.rga.v2-equals-probe",
            Empty = variant.Empty,
            InsertAtHead = variant.InsertAtHead,
            InsertAfter = variant.InsertAfter,
            Remove = variant.Remove,
            Merge = variant.Merge,
            Values = variant.Values
        }, Canonicalize, Sha256);

        Assert.IsFalse(standard.Equals(renamed));
    }


    /// <summary>Pins that two distinct SequenceCheckpointEntry instances with different dots and values are not equal.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryEqualsReturnsFalseForDifferentContentAtADifferentReference()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(1), 1), "A");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(2), 2), "B");

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>Pins that the dot's counter feeds the hash code.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryHashCodeReflectsTheDotCounter()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(7), 1), "A");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(7), 2), "A");

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>
    /// A strategy that certifies no projection cannot apply a committed seal either: the plain non-compacting
    /// RGA strategy throws, just as Seal does.
    /// </summary>
    [TestMethod]
    public void ApplyCommittedSealRequiresACertifyingStrategy()
    {
        CheckpointedSequence<Rga<string>, string, Dot> plainRga =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(WellKnownSequenceStrategies.CreateRga<string>(), Canonicalize, Sha256);
        var committed = new CheckpointCommitment(VectorClock.Empty, new byte[] { 1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => plainRga.ApplyCommittedSeal(committed, new Ballot(1, R1)));
    }


    /// <summary>
    /// Sealing requires both delegates: a context that certifies a projection but does not compact still
    /// refuses to seal, matching a context that does neither.
    /// </summary>
    [TestMethod]
    public void SealRequiresBothCertifyAndCompactIndividually()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var certifiesOnly = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            CertifyProjection = wired.CertifyProjection
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(certifiesOnly, Canonicalize, Sha256).InsertAtHead("A", R1);
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        Assert.ThrowsExactly<InvalidOperationException>(() => withA.Seal(register, new Ballot(1, R1), VectorClock.Empty));
    }


    /// <summary>
    /// A member whose causal context does not dominate the committed frontier must refuse to apply, even when
    /// a permissive CertifyProjection delegate would otherwise let its digest coincide -- the dominance guard
    /// is the only thing standing between a lagging member and a silent, wrong apply.
    /// </summary>
    [TestMethod]
    public void ApplyCommittedSealRefusesAMemberThatHasNotCaughtUp()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var permissive = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = static (sequence, _, _) => sequence,
            CausalContext = wired.CausalContext,
            CertifyProjection = static (_, _) => ImmutableArray<SequenceCheckpointEntry<string>>.Empty
        };
        (CheckpointedSequence<Rga<string>, string, Dot> lagging, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(permissive, Canonicalize, Sha256).InsertAtHead("b", R2);
        ReadOnlyMemory<byte> emptyDigest = Sha256(Canonicalize(ImmutableArray<SequenceCheckpointEntry<string>>.Empty));
        var committed = new CheckpointCommitment(VectorClock.Empty.Increment(R1), emptyDigest);

        Assert.ThrowsExactly<InvalidOperationException>(() => lagging.ApplyCommittedSeal(committed, new Ballot(1, R1)));
    }


    /// <summary>
    /// ApplyCommittedSeal requires both delegates just as Seal does: a context that certifies a projection but
    /// does not compact refuses to apply a committed seal, even one whose digest matches.
    /// </summary>
    [TestMethod]
    public void ApplyCommittedSealRequiresBothCertifyAndCompactIndividually()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var certifiesOnly = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            CertifyProjection = wired.CertifyProjection
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(certifiesOnly, Canonicalize, Sha256).InsertAtHead("A", R1);
        var committed = new CheckpointCommitment(VectorClock.Empty, Sha256(Canonicalize(withA.Live.CertifiedProjection(VectorClock.Empty))));

        Assert.ThrowsExactly<InvalidOperationException>(() => withA.ApplyCommittedSeal(committed, new Ballot(1, R1)));
    }


    /// <summary>
    /// Two containers with different live content are never equal, even though neither reference is null and
    /// neither is the other's own instance.
    /// </summary>
    [TestMethod]
    public void EqualsIsFalseForDifferentLiveContent()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = NewSequence().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = NewSequence().InsertAtHead("B", R2);

        Assert.IsFalse(withA.Equals(withB));
    }


    /// <summary>Pins that a matching counter and value are not enough when the replica bytes differ: both dot conjuncts are required.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryEqualsRequiresTheReplicaBytesToMatchWhenTheCounterAndValueAgree()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(4), 9), "same");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(5), 9), "same");

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>
    /// Adopt validates each of its non-live arguments independently, the same shape as Create's own null
    /// checks.
    /// </summary>
    [TestMethod]
    public void AdoptRejectsNullArguments()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> context = WellKnownSequenceStrategies.CreateRga<string>();

        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Adopt(null!, Canonicalize, Sha256, context.Empty));
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Adopt(context, null!, Sha256, context.Empty));
        Assert.ThrowsExactly<ArgumentNullException>(() => CheckpointedSequence<Rga<string>, string, Dot>.Adopt(context, Canonicalize, null!, context.Empty));
    }


    /// <summary>
    /// A proposal at a frontier behind the already-committed one must be refused even when its digest happens
    /// to coincide with the recorded digest -- only an equal frontier makes a digest match idempotent.
    /// </summary>
    [TestMethod]
    public void ABehindProposalWithACoincidentallyMatchingDigestIsRefused()
    {
        SequenceCrdtContext<Rga<string>, string, Dot> wired = WellKnownSequenceStrategies.CreateRgaRle<string>();
        var constantDigestContext = new SequenceCrdtContext<Rga<string>, string, Dot>
        {
            StrategyId = wired.StrategyId,
            Empty = wired.Empty,
            InsertAtHead = wired.InsertAtHead,
            InsertAfter = wired.InsertAfter,
            Remove = wired.Remove,
            Merge = wired.Merge,
            Values = wired.Values,
            Compact = static (sequence, _, _) => sequence,
            CausalContext = wired.CausalContext,
            //Ignores the frontier it is passed and always certifies over the sequence's own full causal
            //context, so two seals on the same live state propose byte-identical digests no matter which
            //frontier each names -- isolating the frontier-order check from the digest check.
            CertifyProjection = static (sequence, _) => sequence.CertifiedProjection(sequence.CausalContext)
        };
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) =
            CheckpointedSequence<Rga<string>, string, Dot>.Create(constantDigestContext, Canonicalize, Sha256).InsertAtHead("A", R1);
        VectorClock ahead = withA.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);

        (_, CasPaxosRegister<CheckpointCommitment> registerAfterFirst, _, bool firstSealed) =
            withA.Seal(register, new Ballot(1, R1), ahead);
        Assert.IsTrue(firstSealed);

        (_, _, ChangeOutcome<CheckpointCommitment> secondOutcome, bool secondSealed) =
            withA.Seal(registerAfterFirst, new Ballot(2, R1), VectorClock.Empty);

        Assert.IsFalse(secondSealed);
        Assert.AreEqual(ahead, secondOutcome.Value!.Frontier);
    }


    /// <summary>
    /// A reseal at an equal frontier with a matching digest exposes its local proposal instance as the chosen commitment.
    /// </summary>
    [TestMethod]
    public void AResealWithMatchingFrontierAndDigestExposesTheLocalCommitmentInstance()
    {
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = Sealable().InsertAtHead("A", R1);
        VectorClock frontier = withA.CausalContext!;
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        var firstSeal = withA.Seal(register, new Ballot(1, R1), frontier);
        Assert.IsTrue(firstSeal.Sealed);
        Assert.IsNotNull(firstSeal.Outcome.Value);

        var reseal = firstSeal.Sequence.Seal(firstSeal.Register, new Ballot(2, R1), frontier);

        Assert.IsTrue(reseal.Sealed);
        Assert.IsNotNull(reseal.Sequence.Commitment);
        Assert.AreEqual(firstSeal.Outcome.Value, reseal.Sequence.Commitment);
        Assert.AreNotSame(firstSeal.Outcome.Value, reseal.Sequence.Commitment);
        Assert.AreSame(reseal.Sequence.Commitment, reseal.Outcome.Value);
    }

    /// <summary>
    /// Two containers whose checkpoint content is byte-identical (both empty) but whose recorded commitment
    /// differs -- same digest, different frontier -- must not compare equal: an equal checkpoint does not
    /// stand in for an equal commitment.
    /// </summary>
    [TestMethod]
    public void EqualsRequiresMatchingCommitmentNotOnlyCheckpoint()
    {
        CasPaxosRegister<CheckpointCommitment> registerA = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> sealedA, _, _, bool aSealed) =
            Sealable().Seal(registerA, new Ballot(1, R1), VectorClock.Empty);
        Assert.IsTrue(aSealed);

        CasPaxosRegister<CheckpointCommitment> registerB = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> sealedB, _, _, bool bSealed) =
            Sealable().Seal(registerB, new Ballot(1, R1), VectorClock.Empty.Increment(R1));
        Assert.IsTrue(bSealed);

        Assert.IsEmpty(sealedA.Checkpoint);
        Assert.IsEmpty(sealedB.Checkpoint);
        Assert.AreNotEqual(sealedA.Commitment, sealedB.Commitment);
        Assert.IsFalse(sealedA.Equals(sealedB));
    }


    /// <summary>Pins that equal values with a different dot are not equal: the dot conjuncts must also hold.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryEqualsRequiresTheDotToMatchWhenValuesAreEqual()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(3), 7), "left");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(9), 7), "left");

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>Pins that the value feeds the hash code.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryHashCodeReflectsTheValue()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(7), 5), "A");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(7), 5), "B");

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>Pins that the replica bytes feed the hash code, following this repository's AreNotEqual hash-discrimination convention.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryHashCodeReflectsTheReplicaBytes()
    {
        var first = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(1), 3), "A");
        var second = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(2), 3), "A");

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>
    /// UnstableInserts null-checks its frontier through the container's own guard, before consulting the
    /// strategy: a seamless strategy (no wired probe) still throws on a null frontier rather than returning
    /// its null slot.
    /// </summary>
    [TestMethod]
    public void UnstableInsertsRejectsNullFrontierOnASeamlessStrategy()
    {
        CheckpointedSequence<Rga<string>, string, Dot> seamless = NewSequence();

        Assert.ThrowsExactly<ArgumentNullException>(() => seamless.UnstableInserts(null!));
    }


    /// <summary>
    /// Merge keeps this container's own checkpoint when the ballots are tied: the retention rule is
    /// mine >= theirs, so an equal ballot favors the receiver, not the argument.
    /// </summary>
    [TestMethod]
    public void MergeKeepsThisCheckpointOnATiedBallot()
    {
        Ballot tie = new(5, R1);
        CasPaxosRegister<CheckpointCommitment> registerA = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> withA, _) = Sealable().InsertAtHead("a", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> sealedA, _, _, bool aSealed) = withA.Seal(registerA, tie, withA.CausalContext!);

        CasPaxosRegister<CheckpointCommitment> registerB = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = Sealable().InsertAtHead("b", R2);
        (CheckpointedSequence<Rga<string>, string, Dot> sealedB, _, _, bool bSealed) = withB.Seal(registerB, tie, withB.CausalContext!);

        Assert.IsTrue(aSealed);
        Assert.IsTrue(bSealed);
        Assert.AreEqual(tie, sealedA.CheckpointBallot);
        Assert.AreEqual(tie, sealedB.CheckpointBallot);

        CheckpointedSequence<Rga<string>, string, Dot> merged = sealedA.Merge(sealedB);

        Assert.AreEqual(tie, merged.CheckpointBallot);
        Assert.AreEqual(sealedA.Commitment, merged.Commitment);
        string[] expected = ["a"];
        Assert.AreSequenceEqual(expected, CheckpointValues(merged.Checkpoint));
    }


    /// <summary>
    /// Merging the later container with the earlier one -- this call's own checkpoint outranks the argument's --
    /// keeps this container's own checkpoint content, not the argument's: the branch is not always the "other"
    /// arm.
    /// </summary>
    [TestMethod]
    public void MergeKeepsThisCheckpointWhenItsBallotIsHigher()
    {
        CasPaxosRegister<CheckpointCommitment> register = CasPaxosRegister<CheckpointCommitment>.WithAcceptors(3);
        (CheckpointedSequence<Rga<string>, string, Dot> withA, Dot idA) = Sealable().InsertAtHead("A", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> earlier, CasPaxosRegister<CheckpointCommitment> register1, _, _) =
            withA.Seal(register, new Ballot(1, R1), withA.CausalContext!);
        (CheckpointedSequence<Rga<string>, string, Dot> withB, _) = earlier.InsertAfter(idA, "B", R1);
        (CheckpointedSequence<Rga<string>, string, Dot> later, _, _, _) =
            withB.Seal(register1, new Ballot(2, R1), withB.CausalContext!);

        CheckpointedSequence<Rga<string>, string, Dot> merged = later.Merge(earlier);

        Assert.AreEqual(later.CheckpointBallot, merged.CheckpointBallot);
        Assert.AreEqual(later.Commitment, merged.Commitment);
        string[] expected = ["A", "B"];
        Assert.AreSequenceEqual(expected, CheckpointValues(merged.Checkpoint));
    }


    /// <summary>Pins that comparing a checkpoint entry against a null other is never equal, even though the strongly-typed overload accepts null.</summary>
    [TestMethod]
    public void SequenceCheckpointEntryEqualsReturnsFalseForNull()
    {
        var entry = new SequenceCheckpointEntry<string>(new DotState(ImmutableArray.Create<byte>(1), 1), "A");

        Assert.IsFalse(entry.Equals(NullEntry()));
    }


    /// <summary>Returns a typed null so the strongly typed Equals overload is chosen over Equals(object).</summary>
    private static SequenceCheckpointEntry<string>? NullEntry() => null;


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


    /// <summary>
    /// Encodes each dotted entry deterministically as dot replica hex, counter, and value, so equal checkpoints
    /// produce equal canonical bytes on every replica.
    /// </summary>
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
