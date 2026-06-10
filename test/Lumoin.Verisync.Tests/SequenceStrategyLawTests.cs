using System.Collections.Generic;
using System.Collections.Immutable;
using CsCheck;
using Lumoin.Verisync.Core;

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
    protected virtual Gen<(TSequence Sequence, VectorClock Frontier, ImmutableArray<TValue> Checkpoint)>? GenCompactionCase => null;


    /// <summary>
    /// Generates compact/merge commutation cases — two operands at or above a shared frontier, together
    /// with that frontier and the agreed checkpoint. Mandatory for strategies whose context supplies a
    /// compaction delegate; <see langword="null"/> otherwise. The operands are generation-aligned so the
    /// merge of their compactions stays a legal same-generation merge.
    /// </summary>
    protected virtual Gen<(TSequence A, TSequence B, VectorClock Frontier, ImmutableArray<TValue> Checkpoint)>? GenCommutationCase => null;


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

            CollectionAssert.AreEqual(ToArray(Context.Values(input.Sequence)), ToArray(Context.Values(compacted)));
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

            CollectionAssert.AreEqual(ToArray(Context.Values(compactThenMerge)), ToArray(Context.Values(mergeOfCompactions)));
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
            CollectionAssert.AreEqual(ToArray(Context.Values(order1)), ToArray(Context.Values(order2)));
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

            TSequence removed = Context.Remove(input.sequence, target);

            IReadOnlyList<TValue> after = Context.Values(removed);
            Assert.HasCount(before.Count - 1, after);
            var expected = new List<TValue>(before);
            expected.RemoveAt(targetIndex);
            CollectionAssert.AreEqual(ToArray(expected), ToArray(after));
        });
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
