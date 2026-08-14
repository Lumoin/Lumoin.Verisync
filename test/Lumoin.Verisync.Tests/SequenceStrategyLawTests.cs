using CsCheck;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The shared law harness every registered sequence strategy must pass: the join-semilattice laws,
/// convergence under arbitrary merge orders, and local intention preservation — all exercised through
/// the <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}"/> delegates, never the concrete
/// type, so the harness verifies exactly what a container relies on. A strategy registers by deriving
/// a <c>[TestClass]</c> and supplying its context, replica-disjoint generators, and an anchor lookup.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type.</typeparam>
/// <remarks>
/// The compaction laws (visible values unchanged, idempotence at a frontier, compact/merge commutation
/// at or above the frontier, anchor servability across the waterline) are part of this harness, driven
/// by the context's compaction delegates — the harness is the contract the waterline strategies are
/// built against. A strategy that does not compact leaves those delegates null and the four laws
/// early-return.
/// </remarks>
internal abstract class SequenceStrategyLawTests<TSequence, TValue, TAnchor>
{
    /// <summary>The strategy under test.</summary>
    protected abstract SequenceCrdtContext<TSequence, TValue, TAnchor> Context { get; }

    /// <summary>
    /// Generates sequences built by the replica at <paramref name="replicaIndex"/> (0 through 2).
    /// Identity spaces of different indices must be disjoint, and a given identity must map to the
    /// same value across all generated sequences — the invariant every real history maintains.
    /// </summary>
    /// <param name="replicaIndex">The replica index, 0 through 2.</param>
    /// <returns>The generator.</returns>
    protected abstract Gen<TSequence> GenFromReplica(int replicaIndex);

    /// <summary>The replica identity for the given index; index 3 is reserved for the intention test's fresh writer.</summary>
    /// <param name="replicaIndex">The replica index.</param>
    /// <returns>The replica identity.</returns>
    protected abstract ReplicaId Replica(int replicaIndex);

    /// <summary>A sentinel value no generator ever produces, used to locate the intention test's insert.</summary>
    protected abstract TValue FreshValue { get; }

    /// <summary>Resolves the anchor of the visible element at <paramref name="index"/> in <paramref name="sequence"/>.</summary>
    /// <param name="sequence">The sequence to inspect.</param>
    /// <param name="index">The zero-based index into the visible values.</param>
    /// <returns>The element's anchor.</returns>
    protected abstract TAnchor AnchorOfVisibleElement(TSequence sequence, int index);

    /// <summary>
    /// Generates compaction cases — a sequence together with the stability frontier and agreed
    /// checkpoint to compact against. Mandatory for strategies whose context supplies a compaction
    /// delegate; <see langword="null"/> otherwise. The compact/merge commutation law joins these when
    /// the first compacting strategy registers, with an operand generator constrained to at-or-above
    /// the frontier.
    /// </summary>
    protected virtual Gen<(TSequence Sequence, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<TValue>> Checkpoint)>? GenCompactionCase => null;


    /// <summary>
    /// Generates compact/merge commutation cases — two operands at or above a shared frontier, together
    /// with that frontier and the agreed checkpoint. Mandatory for strategies whose context supplies a
    /// compaction delegate; <see langword="null"/> otherwise. The operands are generation-aligned so the
    /// merge of their compactions stays a legal same-generation merge.
    /// </summary>
    protected virtual Gen<(TSequence A, TSequence B, VectorClock Frontier, ImmutableArray<SequenceCheckpointEntry<TValue>> Checkpoint)>? GenCommutationCase => null;


    /// <summary>
    /// Generates a replica-honest op history — inserts and dotted removes — together with a strict prefix
    /// snapshot: <c>Full</c> is the whole history, <c>Behind</c> the state after only the prefix. Abstract
    /// because the no-drop law is universal: every registration supplies it.
    /// </summary>
    protected abstract Gen<(TSequence Full, TSequence Behind)> GenFullAndBehindHistory { get; }


    /// <summary>
    /// Builds the drop-only remove scenario the resurrection and stale-replay laws consume, or
    /// <see langword="null"/> when the strategy does not certify removes. Mandatory for a certifying
    /// strategy; the scenario's compaction must be drop-only so the compacted survivor merges legally with
    /// the uncompacted ghost-holder and stale operands.
    /// </summary>
    protected virtual RemoveScenario? BuildRemoveScenario() => null;


    /// <summary>
    /// Asserts the strategy-shaped half of the remove-observation law: how the removed element's anchor and
    /// the compacted state present at the uncertified frontier versus the certified one. Both frontiers are
    /// carried so a base-materializing strategy can assert its generation stamping. The default is a no-op —
    /// the generic body already asserts the frontier-invariant half.
    /// </summary>
    protected virtual void AssertRemoveConversionOutcome(
        TSequence uncertifiedCompacted,
        TSequence certifiedCompacted,
        TAnchor removedAnchor,
        TAnchor survivorAnchor,
        VectorClock uncertifiedFrontier,
        VectorClock certifiedFrontier)
    {
    }


    /// <summary>
    /// The drop-only remove scenario: a compacted survivor-only state, an uncompacted ghost-holder that
    /// still carries the removed vertex with its dotted tombstone, a stale pre-remove state that holds it
    /// live with no tombstone, and the frontier and checkpoint the compaction ran at.
    /// </summary>
    protected sealed record RemoveScenario(
        TSequence Compacted,
        TSequence GhostHolder,
        TSequence StalePreRemove,
        VectorClock Frontier,
        ImmutableArray<SequenceCheckpointEntry<TValue>> Checkpoint);


    [TestMethod]
    public void TheAnchorTypeCarriesTheFailClosedNull()
    {
        //The translation seam returns TAnchor?, and for an unconstrained type parameter that is a real
        //nullable only when TAnchor is a reference type: a value-type anchor cannot carry the fail-closed
        //null through the seam, and every servability null-assertion in this harness would box it and pass
        //vacuously. Every registered strategy therefore supplies a reference-type anchor.
        Assert.IsTrue(typeof(TAnchor).IsClass, "The anchor type must be a reference type so the translation seam's null and this harness's null assertions are meaningful.");
    }


    [TestMethod]
    public void CompactionPreservesVisibleValues()
    {
        if(Context.Compact is null)
        {
            return;
        }

        Assert.IsNotNull(GenCompactionCase, "A compacting strategy must supply GenCompactionCase.");
        GenCompactionCase.Sample(input =>
        {
            TSequence compacted = Context.Compact(input.Sequence, input.Frontier, input.Checkpoint);

            Assert.AreSequenceEqual(ToArray(Context.Values(input.Sequence)), ToArray(Context.Values(compacted)));
        });
    }


    [TestMethod]
    public void CompactionIsIdempotentAtAFrontier()
    {
        if(Context.Compact is null)
        {
            return;
        }

        Assert.IsNotNull(GenCompactionCase, "A compacting strategy must supply GenCompactionCase.");
        GenCompactionCase.Sample(input =>
        {
            TSequence once = Context.Compact(input.Sequence, input.Frontier, input.Checkpoint);
            TSequence twice = Context.Compact(once, input.Frontier, input.Checkpoint);

            Assert.AreEqual(once, twice);
        });
    }


    [TestMethod]
    public void CompactionCommutesWithMergeAtOrAboveTheFrontier()
    {
        if(Context.Compact is null)
        {
            return;
        }

        Assert.IsNotNull(GenCommutationCase, "A compacting strategy must supply GenCommutationCase.");
        GenCommutationCase.Sample(input =>
        {
            //Law 3 in generation-aligned form: an old-generation operand is brought across by compacting
            //at the same agreed (frontier, checkpoint), which is exactly what the composition does — a raw
            //cross-generation merge fails closed by design.
            TSequence compactThenMerge = Context.Compact(Context.Merge(input.A, input.B), input.Frontier, input.Checkpoint);
            TSequence mergeOfCompactions = Context.Merge(
                Context.Compact(input.A, input.Frontier, input.Checkpoint),
                Context.Compact(input.B, input.Frontier, input.Checkpoint));

            Assert.AreSequenceEqual(ToArray(Context.Values(compactThenMerge)), ToArray(Context.Values(mergeOfCompactions)));
        });
    }


    [TestMethod]
    public void AnchorsResolveEquivalentlyAcrossCompaction()
    {
        if(Context.Compact is null)
        {
            return;
        }

        Assert.IsNotNull(Context.TranslateAnchor, "A compacting strategy must supply a TranslateAnchor delegate.");
        Assert.IsNotNull(GenCompactionCase, "A compacting strategy must supply GenCompactionCase.");
        TranslateAnchorDelegate<TSequence, TAnchor> translateAnchor = Context.TranslateAnchor;
        GenCompactionCase.Sample(input =>
        {
            TSequence compacted = Context.Compact(input.Sequence, input.Frontier, input.Checkpoint);
            int count = Context.Values(input.Sequence).Count;
            for(int i = 0; i < count; i++)
            {
                TAnchor original = AnchorOfVisibleElement(input.Sequence, i);
                TAnchor? translated = translateAnchor(compacted, original);
                Assert.IsNotNull(translated, "Every visible anchor must remain servable across compaction.");

                (TSequence intoOriginal, _) = Context.InsertAfter(input.Sequence, original, FreshValue, Replica(3));
                (TSequence intoCompacted, _) = Context.InsertAfter(compacted, translated!, FreshValue, Replica(3));

                Assert.AreEqual(FreshValue, Context.Values(intoOriginal)[i + 1]);
                Assert.AreEqual(FreshValue, Context.Values(intoCompacted)[i + 1]);
            }
        });
    }


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenFromReplica(0), GenFromReplica(1), (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(Context.Merge(pair.a, pair.b), Context.Merge(pair.b, pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenFromReplica(0), GenFromReplica(1), GenFromReplica(2), (a, b, c) => (a, b, c)).Sample(triple =>
        {
            Assert.AreEqual(
                Context.Merge(Context.Merge(triple.a, triple.b), triple.c),
                Context.Merge(triple.a, Context.Merge(triple.b, triple.c)));
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenFromReplica(0).Sample(sequence =>
        {
            Assert.AreEqual(sequence, Context.Merge(sequence, sequence));
        });
    }


    [TestMethod]
    public void ConvergesRegardlessOfMergeOrder()
    {
        Gen.Select(GenFromReplica(0), GenFromReplica(1), GenFromReplica(2), (a, b, c) => (a, b, c)).Sample(triple =>
        {
            TSequence order1 = Context.Merge(Context.Merge(triple.a, triple.b), triple.c);
            TSequence order2 = Context.Merge(triple.c, Context.Merge(triple.a, triple.b));

            Assert.AreEqual(order1, order2);
            Assert.AreSequenceEqual(ToArray(Context.Values(order1)), ToArray(Context.Values(order2)));
        });
    }


    [TestMethod]
    public void InsertAfterIsIntentionPreservingOverMergedState()
    {
        //Whatever two replicas built and merged, an insert after any visible element must land
        //immediately after that element in the inserting replica's local view.
        Gen.Select(GenFromReplica(0), GenFromReplica(1), Gen.Int[0, 100], (a, b, pick) => (a, b, pick)).Sample(input =>
        {
            TSequence merged = Context.Merge(input.a, input.b);
            IReadOnlyList<TValue> before = Context.Values(merged);
            if(before.Count == 0)
            {
                return;
            }

            int targetIndex = input.pick % before.Count;
            TAnchor target = AnchorOfVisibleElement(merged, targetIndex);
            (TSequence inserted, _) = Context.InsertAfter(merged, target, FreshValue, Replica(3));

            Assert.AreEqual(FreshValue, Context.Values(inserted)[targetIndex + 1]);
        });
    }


    [TestMethod]
    public void InsertAtHeadLandsFirstLocally()
    {
        GenFromReplica(0).Sample(sequence =>
        {
            (TSequence inserted, _) = Context.InsertAtHead(sequence, FreshValue, Replica(3));

            Assert.AreEqual(FreshValue, Context.Values(inserted)[0]);
        });
    }


    [TestMethod]
    public void RemoveHidesExactlyTheAnchoredElement()
    {
        Gen.Select(GenFromReplica(0), Gen.Int[0, 100], (sequence, pick) => (sequence, pick)).Sample(input =>
        {
            IReadOnlyList<TValue> before = Context.Values(input.sequence);
            if(before.Count == 0)
            {
                return;
            }

            int targetIndex = input.pick % before.Count;
            TAnchor target = AnchorOfVisibleElement(input.sequence, targetIndex);

            TSequence removed = Context.Remove(input.sequence, target, Replica(3));

            IReadOnlyList<TValue> after = Context.Values(removed);
            Assert.HasCount(before.Count - 1, after);
            var expected = new List<TValue>(before);
            expected.RemoveAt(targetIndex);
            Assert.AreSequenceEqual(ToArray(expected), ToArray(after));
        });
    }


    /// <summary>
    /// LAW-NFD: merging an empty or a strictly-behind operand never drops a live element — the merge equals the
    /// full history's visible values in every order.
    /// </summary>
    /// <remarks>
    /// Runs for every strategy, no capability gate.
    /// </remarks>
    [TestMethod]
    public void MergeWithEmptyOrABehindOperandNeverDropsALiveElement()
    {
        GenFullAndBehindHistory.Sample(input =>
        {
            TValue[] full = ToArray(Context.Values(input.Full));
            Assert.AreSequenceEqual(full, ToArray(Context.Values(Context.Merge(input.Full, Context.Empty))));
            Assert.AreSequenceEqual(full, ToArray(Context.Values(Context.Merge(Context.Empty, input.Full))));
            Assert.AreSequenceEqual(full, ToArray(Context.Values(Context.Merge(input.Full, input.Behind))));
            Assert.AreSequenceEqual(full, ToArray(Context.Values(Context.Merge(input.Behind, input.Full))));
        });
    }


    /// <summary>
    /// LAW-RG: a remove only gates a drop once it is certified group-wide.
    /// </summary>
    /// <remarks>
    /// A head-insert survivor and a childless element after it, the element removed; at the uncertified
    /// frontier the projection still carries the hidden value and the drop does not fire, at the certified
    /// frontier it does. The strategy-shaped conversion outcome — ghost-in-place versus pending-removed base
    /// conversion — is asserted through the hook, which carries both frontiers.
    /// </remarks>
    [TestMethod]
    public void RemoveObservationGatesTheDrop()
    {
        if(Context.CertifyProjection is null || Context.Compact is null || Context.TranslateAnchor is null || Context.CausalContext is null)
        {
            return;
        }

        CertifySequenceProjectionDelegate<TSequence, TValue> certifyProjection = Context.CertifyProjection;
        CompactSequenceDelegate<TSequence, TValue> compact = Context.Compact;
        TranslateAnchorDelegate<TSequence, TAnchor> translateAnchor = Context.TranslateAnchor;
        SequenceCausalContextDelegate<TSequence> causalContext = Context.CausalContext;

        (TSequence withA, TAnchor anchorA) = Context.InsertAtHead(Context.Empty, FreshValue, Replica(1));
        (TSequence withB, TAnchor anchorB) = Context.InsertAfter(withA, anchorA, FreshValue, Replica(1));
        TSequence withRemove = Context.Remove(withB, anchorB, Replica(1));

        //f1 folds the laggard's PRE-remove context, so the min floors the removing axis below the remove-dot:
        //both inserts covered (insert-quiescent for offset), the remove not certified. f2 certifies it.
        VectorClock preRemove = causalContext(withB);
        VectorClock postRemove = causalContext(withRemove);
        VectorClock f1 = Frontier(postRemove, postRemove, preRemove);
        VectorClock f2 = Frontier(postRemove, postRemove, postRemove);

        //The projection at f1 still carries the locally hidden removed element; at f2 the remove is certified
        //and it leaves. The survivor and removed values are both the sentinel — LAW-RG certifies the gating,
        //not value distinctness, and the strategy-shaped structure is the hook's concern.
        ImmutableArray<SequenceCheckpointEntry<TValue>> projectionAtF1 = certifyProjection(withRemove, f1);
        ImmutableArray<SequenceCheckpointEntry<TValue>> projectionAtF2 = certifyProjection(withRemove, f2);
        TValue[] bothValues = [FreshValue, FreshValue];
        TValue[] survivorOnly = [FreshValue];
        Assert.AreSequenceEqual(bothValues, ProjectionValues(projectionAtF1));
        Assert.AreSequenceEqual(survivorOnly, ProjectionValues(projectionAtF2));

        TSequence compactedAtF1 = compact(withRemove, f1, projectionAtF1);
        TSequence compactedAtF2 = compact(withRemove, f2, projectionAtF2);
        Assert.AreSequenceEqual(survivorOnly, ToArray(Context.Values(compactedAtF1)));
        Assert.AreSequenceEqual(survivorOnly, ToArray(Context.Values(compactedAtF2)));
        Assert.IsNotNull(translateAnchor(compactedAtF1, anchorB));
        Assert.IsNotNull(translateAnchor(compactedAtF2, anchorB));

        AssertRemoveConversionOutcome(compactedAtF1, compactedAtF2, anchorB, anchorA, f1, f2);
    }


    /// <summary>
    /// LAW-NR: merging a former laggard that holds the removed vertex and its dotted tombstone re-enters the
    /// ghost hidden, never resurrecting the committed remove; recompacting either merge order returns the
    /// compacted state.
    /// </summary>
    [TestMethod]
    public void MergeDoesNotResurrectACommittedRemove()
    {
        if(Context.CertifyProjection is null || Context.Compact is null || Context.CausalContext is null)
        {
            return;
        }

        CompactSequenceDelegate<TSequence, TValue> compact = Context.Compact;
        RemoveScenario? scenario = BuildRemoveScenario();
        Assert.IsNotNull(scenario, "A certifying strategy must supply BuildRemoveScenario.");
        TSequence x = scenario.Compacted;
        TSequence y = scenario.GhostHolder;

        TValue[] visible = ToArray(Context.Values(x));
        Assert.AreSequenceEqual(visible, ToArray(Context.Values(Context.Merge(x, y))));
        Assert.AreSequenceEqual(visible, ToArray(Context.Values(Context.Merge(y, x))));
        Assert.AreEqual(x, compact(Context.Merge(x, y), scenario.Frontier, scenario.Checkpoint));
        Assert.AreEqual(x, compact(Context.Merge(y, x), scenario.Frontier, scenario.Checkpoint));
    }


    /// <summary>
    /// LAW-SR: a stale pre-remove state (the removed element live, no tombstone) fails the stale-replay
    /// detector closed in both merge orders, while the honest ghost-holder throws in neither.
    /// </summary>
    [TestMethod]
    public void AStalePreRemoveStateFailsClosedInBothMergeOrders()
    {
        if(Context.CertifyProjection is null || Context.Compact is null || Context.CausalContext is null)
        {
            return;
        }

        RemoveScenario? scenario = BuildRemoveScenario();
        Assert.IsNotNull(scenario, "A certifying strategy must supply BuildRemoveScenario.");
        TSequence x = scenario.Compacted;
        TSequence z = scenario.StalePreRemove;
        TSequence y = scenario.GhostHolder;

        Assert.ThrowsExactly<InvalidOperationException>(() => Context.Merge(x, z));
        Assert.ThrowsExactly<InvalidOperationException>(() => Context.Merge(z, x));

        //The honest ghost-holder is not stale: it re-enters the ghost with its tombstone and never throws.
        TValue[] visible = ToArray(Context.Values(x));
        Assert.AreSequenceEqual(visible, ToArray(Context.Values(Context.Merge(x, y))));
        Assert.AreSequenceEqual(visible, ToArray(Context.Values(Context.Merge(y, x))));
    }


    private static TValue[] ProjectionValues(ImmutableArray<SequenceCheckpointEntry<TValue>> projection)
    {
        var values = new TValue[projection.Length];
        for(int i = 0; i < projection.Length; i++)
        {
            values[i] = projection[i].Value;
        }

        return values;
    }


    /// <summary>
    /// Folds the shipped min-fold over one gossip digest per member context; distinct origins do not affect the
    /// element-wise minimum but keep the digests honest.
    /// </summary>
    private static VectorClock Frontier(params VectorClock[] memberContexts)
    {
        var digests = new List<GossipDigest>(memberContexts.Length);
        for(int i = 0; i < memberContexts.Length; i++)
        {
            digests.Add(new GossipDigest(MakeReplica((byte)(200 + i)), memberContexts[i]));
        }

        return StabilityFrontier.Compute(digests);
    }


    private static ReplicaId MakeReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private static TValue[] ToArray(IReadOnlyList<TValue> values)
    {
        var array = new TValue[values.Count];
        for(int i = 0; i < values.Count; i++)
        {
            array[i] = values[i];
        }

        return array;
    }
}
