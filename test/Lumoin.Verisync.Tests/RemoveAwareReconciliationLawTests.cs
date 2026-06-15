using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The algebraic laws of the remove-aware (dot-cloud) anti-entropy session, proven against
/// <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/> as the oracle. A pair of
/// in-memory sessions reconcile two diverged <see cref="DottedVersionVectorSet{T}"/> snapshots over a
/// <see cref="DottedReconciliationProjection{T}"/>: the initiator decodes the symmetric difference of the
/// two present-entry digest sets and classifies each decoded dot against the peer's exchanged causal
/// context, the responder serves fetches with elements only, and both fold the peer context so each ends at
/// its own context merged with the peer's. The master law is that reconcile-then-apply equals
/// <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/>, including the resurrection case
/// the design critique caught — an entry the initiator observed-and-removed but the responder still holds
/// must not come back. The companion laws cover the context fold on the quiescent path, idempotence across a
/// second back-to-back session, frame-level quiescence on equal snapshots, the add-only degeneracy with a
/// <see langword="null"/> local context, and the pooled rental ledger balancing end to end.
/// </summary>
/// <remarks>
/// The host classification logic mirrors <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/>
/// exactly. A decoded item the initiator holds is a present-here, absent-there dot: if the peer's context
/// covers it the peer observed-and-removed it, so it is dropped locally; otherwise it is pushed for the peer
/// to add. A decoded item the initiator lacks is fetched; the fetch answer carries the dot, and if the
/// initiator's own context covers it the initiator observed-and-removed it, so it is pushed as a drop rather
/// than re-added (the resurrection guard); otherwise it is added locally. The uniform apply rule, on both
/// roles, returns as push-drops any received entry the local pre-fold context already covers and adds the
/// rest, then folds the peer context. The session manipulates <see cref="DottedVersionVectorSetState{TValue}"/>
/// directly and rebuilds with <see cref="DottedVersionVectorSet{T}.FromState"/>, folding the peer context
/// together with every insert so the merged context dominates every retained dot, as the reconstruction
/// invariant requires.
/// </remarks>
[TestClass]
internal sealed class RemoveAwareReconciliationLawTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 100;

    private const int DefaultBatchSize = 4;

    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    private static string[] ExpectedSixElements { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task MasterLawReconcileThenApplyEqualsMergeAcrossDivergenceShapes()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The master law is swept over add-only, remove-only, and mixed divergence in one method, since the
        //test runner takes plain methods; each shape generates a known divergence whose merge is the oracle.
        DivergenceShape[] shapes = [DivergenceShape.AddOnly, DivergenceShape.RemoveOnly, DivergenceShape.Mixed];
        foreach(DivergenceShape shape in shapes)
        {
            (DottedVersionVectorSet<string> initiatorStart, DottedVersionVectorSet<string> responderStart) = BuildDivergence(shape);

            //The oracle: the causal merge both replicas must reach is symmetric, so either order is the same value.
            DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
            Assert.AreEqual(expected, responderStart.Merge(initiatorStart), $"The merge oracle is asymmetric for {shape}.");

            (RemoveAwareReconciliationHost initiatorHost, RemoveAwareReconciliationHost responderHost) = await ReconcileOnceAsync(initiatorStart, responderStart, cancellationToken).ConfigureAwait(false);

            Assert.AreEqual(expected, initiatorHost.Current, $"The initiator did not reconcile to the merge for {shape}.");
            Assert.AreEqual(expected, responderHost.Current, $"The responder did not reconcile to the merge for {shape}.");

            //The mixed shape includes a dot the initiator observed-and-removed that the responder still holds; the
            //merge drops it on both sides, so neither replica may resurrect it.
            if(shape == DivergenceShape.Mixed)
            {
                Assert.DoesNotContain(ResurrectionProbe, initiatorHost.Current.Values);
                Assert.DoesNotContain(ResurrectionProbe, responderHost.Current.Values);
            }
        }
    }


    [TestMethod]
    public async Task ContextLawBothEndAtTheMergedContextEvenWhenNoEntriesChange()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Equal entry sets but divergent contexts: both replicas hold the same dots, yet each has observed (and
        //removed) an element the other never saw, so their causal contexts differ while their values do not.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha").Add(R1, "beta");
        DottedVersionVectorSet<string> initiatorStart = shared.Add(R2, "initiatorGhost").RemoveValue("initiatorGhost");
        DottedVersionVectorSet<string> responderStart = shared.Add(R3, "responderGhost").RemoveValue("responderGhost");

        VectorClock expectedContext = initiatorStart.Context.Merge(responderStart.Context);

        (RemoveAwareReconciliationHost initiatorHost, RemoveAwareReconciliationHost responderHost) = await ReconcileOnceAsync(initiatorStart, responderStart, cancellationToken).ConfigureAwait(false);

        //The values were already equal, so the terminal merge must still fire to advance both contexts.
        Assert.AreEqual(expectedContext, initiatorHost.Current.Context);
        Assert.AreEqual(expectedContext, responderHost.Current.Context);
        Assert.AreEqual(initiatorStart.Merge(responderStart), initiatorHost.Current);
        Assert.AreEqual(initiatorStart.Merge(responderStart), responderHost.Current);
    }


    [TestMethod]
    public async Task NoResurrectionARemovedEntryStaysAbsentAcrossASecondSession()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The initiator observed and removed an entry the responder still holds. The first session must drop it
        //on both sides; a second back-to-back session over the converged states must leave it absent, proving
        //the drop is idempotent and never resurrects.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha");
        DottedVersionVectorSet<string> withGhost = shared.Add(R2, ResurrectionProbe);
        DottedVersionVectorSet<string> initiatorStart = withGhost.RemoveValue(ResurrectionProbe);
        DottedVersionVectorSet<string> responderStart = withGhost.Add(R3, "gamma");

        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
        Assert.DoesNotContain(ResurrectionProbe, expected.Values);

        (RemoveAwareReconciliationHost initiatorHost, RemoveAwareReconciliationHost responderHost) = await ReconcileOnceAsync(initiatorStart, responderStart, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, initiatorHost.Current);
        Assert.AreEqual(expected, responderHost.Current);
        Assert.DoesNotContain(ResurrectionProbe, initiatorHost.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, responderHost.Current.Values);

        //A second session over the now-converged states is fully quiescent and the ghost stays gone.
        (RemoveAwareReconciliationHost initiatorAgain, RemoveAwareReconciliationHost responderAgain) = await ReconcileOnceAsync(initiatorHost.Current, responderHost.Current, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, initiatorAgain.Current);
        Assert.AreEqual(expected, responderAgain.Current);
        Assert.DoesNotContain(ResurrectionProbe, initiatorAgain.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, responderAgain.Current.Values);
    }


    [TestMethod]
    public async Task QuiescenceEqualSnapshotsExchangeNoFetchElementsOrDropFrames()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Identical snapshots on both sides: the symmetric difference is empty, so no entry frame crosses; only
        //the offer, the causal-context exchange, and the done marker do.
        DottedVersionVectorSet<string> snapshot = DvvSet().Add(R1, "alpha").Add(R1, "beta").Add(R2, "gamma");

        RemoveAwareReconciliationHost initiatorHost = new(snapshot);
        RemoveAwareReconciliationHost responderHost = new(snapshot);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, null, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, null, localContext: responderHost.LocalContext);

        FrameCensus toResponder = new();
        FrameCensus toInitiator = new();

        Task initiatorRun = initiator.RunAsync(
            toResponder.Wrap(responder),
            initiatorHost.ResolveDifference,
            null,
            initiatorHost.ApplyElements,
            applyDrops: initiatorHost.ApplyDrops,
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        Task responderRun = responder.RunAsync(
            toInitiator.Wrap(initiator),
            null,
            responderHost.ServeFetch,
            responderHost.ApplyElements,
            applyDrops: responderHost.ApplyDrops,
            mergeContext: responderHost.MergeContext,
            cancellationToken: cancellationToken);

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.HasCount(0, initiator.DecodedItems);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
        Assert.AreEqual(AntiEntropySessionState.Completed, responder.State);

        //No fetch, elements, or drop frame crosses in either direction; only offer, context, and done do.
        Assert.AreEqual(0, toResponder.FetchOrElementsOrDrop);
        Assert.AreEqual(0, toInitiator.FetchOrElementsOrDrop);

        //The terminal merge still fires on this quiescent path: both contexts reach the merged context.
        VectorClock expectedContext = snapshot.Context.Merge(snapshot.Context);
        Assert.AreEqual(expectedContext, initiatorHost.Current.Context);
        Assert.AreEqual(expectedContext, responderHost.Current.Context);
        Assert.AreEqual(snapshot, initiatorHost.Current);
        Assert.AreEqual(snapshot, responderHost.Current);
    }


    [TestMethod]
    public async Task AddOnlyDegeneracyWithNullLocalContextConvergesElementSetsAndSendsNoContextOrDropFrames()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The add-only socket-proof shape: a shared ancestor plus a per-side surplus, with a null local context
        //so the sessions are byte-for-byte the add-only path — no context exchange, no drop frame.
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems);

        Dictionary<string, DottedEntry<string>> initiatorDirectory = BuildEntryDirectory(initiatorSet);
        Dictionary<string, DottedEntry<string>> responderDirectory = BuildEntryDirectory(responderSet);
        HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

        //The add-only resolver partitions the decoded difference into a fetch for digests it lacks and a push
        //for digests it holds; the empty peer context is ignored and no local drops arise.
        ResolveReconciliationDifferenceDelegate<DottedEntry<string>> resolve = (decoded, _) =>
        {
            ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
            ImmutableArray<ReconciliationElementEntry<DottedEntry<string>>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<DottedEntry<string>>>();
            foreach(ReadOnlyMemory<byte> item in decoded)
            {
                string hex = Convert.ToHexString(item.Span);
                if(initiatorHexes.Contains(hex))
                {
                    push.Add(new ReconciliationElementEntry<DottedEntry<string>>(item, initiatorDirectory[hex]));
                }
                else
                {
                    fetch.Add(item);
                }
            }

            return new ReconciliationDifferenceResolution<DottedEntry<string>>(fetch.ToImmutable(), push.ToImmutable());
        };

        ServeReconciliationFetchDelegate<DottedEntry<string>> serve = items =>
            [.. items.Select(item => new ReconciliationElementEntry<DottedEntry<string>>(item, responderDirectory[Convert.ToHexString(item.Span)]))];

        HashSet<string> initiatorElements = [.. initiatorSet.Elements];
        ApplyReconciliationElementsDelegate<DottedEntry<string>> applyToInitiator = (entries, _, _) =>
        {
            foreach(ReconciliationElementEntry<DottedEntry<string>> entry in entries)
            {
                initiatorElements.Add(entry.Element.Value);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        HashSet<string> responderElements = [.. responderSet.Elements];
        ApplyReconciliationElementsDelegate<DottedEntry<string>> applyToResponder = (entries, _, _) =>
        {
            foreach(ReconciliationElementEntry<DottedEntry<string>> entry in entries)
            {
                responderElements.Add(entry.Element.Value);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        FrameCensus toResponder = new();
        FrameCensus toInitiator = new();

        Task initiatorRun = initiator.RunAsync(toResponder.Wrap(responder), resolve, null, applyToInitiator, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(toInitiator.Wrap(initiator), null, serve, applyToResponder, cancellationToken: cancellationToken);

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        string[] expected = ExpectedSixElements;
        CollectionAssert.AreEqual(expected, initiatorElements.Order().ToArray());
        CollectionAssert.AreEqual(expected, responderElements.Order().ToArray());
        Assert.HasCount(3, initiator.DecodedItems);

        //A null local context is the add-only path: no context frame and no drop frame crosses in either direction.
        Assert.AreEqual(0, toResponder.ContextOrDrop);
        Assert.AreEqual(0, toInitiator.ContextOrDrop);
    }


    [TestMethod]
    [DoNotParallelize]
    public async Task MemoryAFullRemoveAwareSessionLeavesNoActiveRentals()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            (DottedVersionVectorSet<string> initiatorStart, DottedVersionVectorSet<string> responderStart) = BuildDivergence(DivergenceShape.Mixed);

            DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);

            RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
            RemoveAwareReconciliationHost responderHost = new(responderStart);

            //Inject the pool into both sessions through the pool-bearing constructor; each builds its encoder and
            //(for the initiator) decoder over the pool and disposes them when the session is disposed.
            using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, pool, localContext: initiatorHost.LocalContext);
            using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, pool, localContext: responderHost.LocalContext);

            Task initiatorRun = initiator.RunAsync(
                Forward(responder),
                initiatorHost.ResolveDifference,
                null,
                initiatorHost.ApplyElements,
                applyDrops: initiatorHost.ApplyDrops,
                mergeContext: initiatorHost.MergeContext,
                cancellationToken: cancellationToken);

            Task responderRun = responder.RunAsync(
                Forward(initiator),
                null,
                responderHost.ServeFetch,
                responderHost.ApplyElements,
                applyDrops: responderHost.ApplyDrops,
                mergeContext: responderHost.MergeContext,
                cancellationToken: cancellationToken);

            await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

            responder.Complete();
            await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

            //Convergence proves the pooled cell backings carried the reconciliation, not merely that they balanced.
            Assert.AreEqual(expected, initiatorHost.Current);
            Assert.AreEqual(expected, responderHost.Current);
        }

        //Both sessions and the pool are disposed at the end of the scope, so every rental the sessions took is
        //returned: the net active gauge balances to zero and the rented count equals the returned count and is
        //strictly positive.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    //Runs one remove-aware session between two hosts and returns them with their converged states. The hosts
    //wire the initiator's classifier and both sides' apply, drop, and merge hooks over their own mutable
    //DVVSet, and pass projection.Context as the session's local context.
    private static async Task<(RemoveAwareReconciliationHost Initiator, RemoveAwareReconciliationHost Responder)> ReconcileOnceAsync(
        DottedVersionVectorSet<string> initiatorStart,
        DottedVersionVectorSet<string> responderStart,
        CancellationToken cancellationToken)
    {
        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, null, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, null, localContext: responderHost.LocalContext);

        Task initiatorRun = initiator.RunAsync(
            Forward(responder),
            initiatorHost.ResolveDifference,
            null,
            initiatorHost.ApplyElements,
            applyDrops: initiatorHost.ApplyDrops,
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        Task responderRun = responder.RunAsync(
            Forward(initiator),
            null,
            responderHost.ServeFetch,
            responderHost.ApplyElements,
            applyDrops: responderHost.ApplyDrops,
            mergeContext: responderHost.MergeContext,
            cancellationToken: cancellationToken);

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        return (initiatorHost, responderHost);
    }


    //Builds a known divergence with the merge known by construction. A shared ancestor seeds both sides; the
    //shapes layer per-side adds and an observed remove on top, including the resurrection probe — a dot the
    //initiator observed and removed while the responder still holds it.
    private static (DottedVersionVectorSet<string> Initiator, DottedVersionVectorSet<string> Responder) BuildDivergence(DivergenceShape shape)
    {
        DottedVersionVectorSet<string> ancestor = DvvSet().Add(R1, "alpha").Add(R1, "beta").Add(R1, "gamma");

        return shape switch
        {
            //Pure adds on each side over the shared ancestor; no removes anywhere.
            DivergenceShape.AddOnly => (
                ancestor.Add(R2, "delta").Add(R2, "epsilon"),
                ancestor.Add(R3, "zeta")),

            //Both sides start from a common superset, then each removes a distinct element it observed.
            DivergenceShape.RemoveOnly => RemoveOnlyDivergence(ancestor),

            //A mix: each side adds, and the initiator removes the resurrection probe that the responder retains.
            _ => MixedDivergence(ancestor),
        };
    }


    private static (DottedVersionVectorSet<string> Initiator, DottedVersionVectorSet<string> Responder) RemoveOnlyDivergence(DottedVersionVectorSet<string> ancestor)
    {
        //A common superset both observe, then a distinct observed remove on each side; no adds in the divergence.
        DottedVersionVectorSet<string> superset = ancestor.Add(R2, "delta").Add(R3, "epsilon");
        DottedVersionVectorSet<string> initiator = superset.RemoveValue("delta");
        DottedVersionVectorSet<string> responder = superset.RemoveValue("epsilon");

        return (initiator, responder);
    }


    private static (DottedVersionVectorSet<string> Initiator, DottedVersionVectorSet<string> Responder) MixedDivergence(DottedVersionVectorSet<string> ancestor)
    {
        //The responder mints and keeps the probe under R3; the initiator observes that exact dot, then removes
        //it, while also adding an element of its own. The merge drops the probe on both sides.
        DottedVersionVectorSet<string> withProbe = ancestor.Add(R3, ResurrectionProbe);
        DottedVersionVectorSet<string> initiator = withProbe.Add(R2, "delta").RemoveValue(ResurrectionProbe);
        DottedVersionVectorSet<string> responder = withProbe.Add(R3, "zeta");

        return (initiator, responder);
    }


    private static async Task PaceUntilInitiatorCompletesAsync(AntiEntropySession<DottedEntry<string>> initiator, AntiEntropySession<DottedEntry<string>> responder, CancellationToken cancellationToken)
    {
        int triggers = 0;
        while(initiator.State != AntiEntropySessionState.Completed)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The initiator never completed within the trigger cap.");
        }
    }


    private static SendReconciliationEnvelopeDelegate<DottedEntry<string>> Forward(AntiEntropySession<DottedEntry<string>> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    private static ValueTask ForwardTo(AntiEntropySession<DottedEntry<string>> peer, ReconciliationEnvelope<DottedEntry<string>> envelope, CancellationToken cancellationToken)
    {
        try
        {
            return peer.SubmitAsync(envelope, cancellationToken);
        }
        catch(ChannelClosedException)
        {
            //A completed peer is a wound-down session; dropping the late send is exactly the transport's behaviour.
            return ValueTask.CompletedTask;
        }
    }


    private static ReadOnlyMemory<byte>[] ProjectHashes(OrSet<string> set)
    {
        List<ReadOnlyMemory<byte>> items = [];
        foreach(string element in set.Elements)
        {
            items.Add(SHA256.HashData(Encoding.UTF8.GetBytes(element)));
        }

        return [.. items];
    }


    private static Dictionary<string, DottedEntry<string>> BuildEntryDirectory(OrSet<string> set)
    {
        //The add-only degeneracy resolves each element to a synthetic dotted entry keyed by its item hex; the
        //dot is never inspected on this path, only the carried value.
        DottedVersionVectorSetState<string> state = set.ToState().Set;
        Dictionary<string, DottedEntry<string>> directory = [];
        foreach(DottedEntry<string> entry in state.Entries)
        {
            string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value)));
            directory[hex] = entry;
        }

        return directory;
    }


    private static DottedVersionVectorSet<string> DvvSet() => DottedVersionVectorSet<string>.Empty;


    private const string ResurrectionProbe = "ghost";


    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    //The three divergence shapes the master law sweeps: pure adds, pure observed removes, and a mix that
    //includes the resurrection probe.
    internal enum DivergenceShape
    {
        AddOnly,
        RemoveOnly,
        Mixed,
    }


    //Counts the entry-bearing and remove-aware frames a send delegate carries, so a test can assert which
    //payloads crossed the wire. The session serializes its sends through the single consumer loop, so the
    //plain increments need no synchronization.
    private sealed class FrameCensus
    {
        public int FetchOrElementsOrDrop { get; private set; }

        public int ContextOrDrop { get; private set; }


        public SendReconciliationEnvelopeDelegate<DottedEntry<string>> Wrap(AntiEntropySession<DottedEntry<string>> peer)
        {
            return (envelope, cancellationToken) =>
            {
                if(envelope.Fetch is not null || envelope.Elements is not null || envelope.Drop is not null)
                {
                    FetchOrElementsOrDrop++;
                }

                if(envelope.Context is not null || envelope.Drop is not null)
                {
                    ContextOrDrop++;
                }

                return ForwardTo(peer, envelope, cancellationToken);
            };
        }
    }


}
