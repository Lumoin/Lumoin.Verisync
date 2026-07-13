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
/// context, the responder serves fetches with elements only, and each side folds the peer context only
/// alongside the applies that carry entries or drops — the initiator alone folds terminally, when it
/// completes an exchange in which no apply folded, because only the decoder knows the exchange finished. The
/// master law is that reconcile-then-apply equals
/// <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/>, including the resurrection case
/// the design critique caught — an entry the initiator observed-and-removed but the responder still holds
/// must not come back. The companion laws cover one-session bidirectional context convergence — the responder
/// now folds the initiator's exchanged context on the completion frame, so a single session converges both
/// contexts, with a reverse session only confirming quiescence — the completion frame surviving a wind-down as
/// a preserved Completed with the folded context intact, an interrupted initiator emitting no completion frame,
/// no false drop in a follow-up session on the completed path, the interrupted wind-down folding nothing (the
/// false-drop guard, on both a responder and an initiator whose local drops defer while its fetch is
/// outstanding), the responder failing closed on a missing peer context, idempotence across a
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
    public async Task TerminalCompletedImpliesConvergedOnBothRolesHappyPath()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The reconciled invariant's positive direction: at a terminal state Completed and IsConverged agree.
        //A full remove-aware exchange over a real divergence drives the initiator through its fetch-answered
        //completion and the responder through the done signal, so both end Completed and both attest converged.
        (DottedVersionVectorSet<string> initiatorStart, DottedVersionVectorSet<string> responderStart) = BuildDivergence(DivergenceShape.Mixed);
        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);

        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

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

        //Both reached the merged state, both land at the terminal Completed, and both attest convergence: the
        //Completed-implies-converged half of the invariant holds on both roles.
        Assert.AreEqual(expected, initiatorHost.Current);
        Assert.AreEqual(expected, responderHost.Current);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
        Assert.AreEqual(AntiEntropySessionState.Completed, responder.State);
        Assert.IsTrue(initiator.IsConverged, "A terminal Completed initiator must attest convergence.");
        Assert.IsTrue(responder.IsConverged, "A terminal Completed responder must attest convergence.");
    }


    [TestMethod]
    public async Task ContextLawACompletedExchangeConvergesBothContextsInOneSession()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Equal entry sets but divergent contexts: both replicas hold the same dots, yet each has observed (and
        //removed) an element the other never saw, so their causal contexts differ while their values do not. The
        //completion frame closes the gap the reverse session used to fill — the responder folds the initiator's
        //exchanged context on a verified-complete exchange, so ONE session converges both contexts.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha").Add(R1, "beta");
        DottedVersionVectorSet<string> initiatorStart = shared.Add(R2, "initiatorGhost").RemoveValue("initiatorGhost");
        DottedVersionVectorSet<string> responderStart = shared.Add(R3, "responderGhost").RemoveValue("responderGhost");

        VectorClock expectedContext = initiatorStart.Context.Merge(responderStart.Context);
        DottedVersionVectorSet<string> expectedValue = initiatorStart.Merge(responderStart);

        (RemoveAwareReconciliationHost initiatorHost, RemoveAwareReconciliationHost responderHost) = await ReconcileOnceAsync(initiatorStart, responderStart, cancellationToken).ConfigureAwait(false);

        //The initiator folds terminally on its completed decode, and the responder folds on the completion frame:
        //both end at the merged context in a single session, and both values match the oracle.
        Assert.AreEqual(expectedContext, initiatorHost.Current.Context);
        Assert.AreEqual(expectedContext, responderHost.Current.Context);
        Assert.AreEqual(expectedValue, initiatorHost.Current);
        Assert.AreEqual(expectedValue, responderHost.Current);

        //The quiescence strengthening: a reverse session between the now-converged states transfers nothing —
        //equal snapshots leave the symmetric difference empty, so no fetch, elements, or drop frame crosses in
        //either direction — and both replicas stay at the merge, confirming the one-session fold was complete.
        (RemoveAwareReconciliationHost reverseInitiator, RemoveAwareReconciliationHost reverseResponder, FrameCensus reverseToResponder, FrameCensus reverseToInitiator) =
            await ReconcileOnceWithCensusAsync(responderHost.Current, initiatorHost.Current, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, reverseToResponder.FetchOrElementsOrDrop);
        Assert.AreEqual(0, reverseToInitiator.FetchOrElementsOrDrop);
        Assert.AreEqual(expectedContext, reverseInitiator.Current.Context);
        Assert.AreEqual(expectedContext, reverseResponder.Current.Context);
        Assert.AreEqual(expectedValue, reverseInitiator.Current);
        Assert.AreEqual(expectedValue, reverseResponder.Current);
    }


    [TestMethod]
    public async Task AWindDownAfterTheCompletionFrameStaysCompletedAndConverged()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The drain-preservation pin: the responder reaches Completed on the completion frame with the initiator's
        //context folded, THEN the host winds it down with Complete(). The drain must keep the frame-earned
        //Completed and the folded context rather than overwrite them with Interrupted. Divergent contexts over
        //equal entry sets isolate the fold — nothing transfers, so only the completion frame can advance the
        //responder's context.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha").Add(R1, "beta");
        DottedVersionVectorSet<string> initiatorStart = shared.Add(R2, "initiatorGhost").RemoveValue("initiatorGhost");
        DottedVersionVectorSet<string> responderStart = shared.Add(R3, "responderGhost").RemoveValue("responderGhost");

        VectorClock expectedContext = initiatorStart.Context.Merge(responderStart.Context);
        DottedVersionVectorSet<string> expectedValue = initiatorStart.Merge(responderStart);

        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

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

        //The responder stayed Completed through the wind-down, still attests convergence, and kept the folded
        //context — the merged context, reachable on this quiescent exchange only through the completion frame.
        Assert.AreEqual(AntiEntropySessionState.Completed, responder.State);
        Assert.IsTrue(responder.IsConverged, "A responder that folded on the completion frame attests convergence.");
        Assert.AreEqual(expectedContext, responderHost.Current.Context);
        Assert.AreEqual(expectedValue, responderHost.Current);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
    }


    [TestMethod]
    public async Task AnInterruptedInitiatorNeverEmitsACompletionFrame()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //TC-1 census: the completion frame is emitted only at the initiator's two Completed transitions, both
        //structurally unreachable from a drain. An initiator wound down before completing must emit none, at every
        //pre-Completed phase.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha");
        DottedVersionVectorSet<string> responderStart = shared.Add(R3, "unicorn");

        //Phase A: interrupted before the decode completes — only the offer and the context ever went out.
        {
            RemoveAwareReconciliationHost initiatorHost = new(shared);
            using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

            int completions = 0;
            SendReconciliationEnvelopeDelegate<DottedEntry<string>> countingVoid = (envelope, token) =>
            {
                if(envelope.Completion is not null)
                {
                    completions++;
                }

                return ValueTask.CompletedTask;
            };

            Task initiatorRun = initiator.RunAsync(
                countingVoid,
                initiatorHost.ResolveDifference,
                null,
                initiatorHost.ApplyElements,
                applyDrops: initiatorHost.ApplyDrops,
                mergeContext: initiatorHost.MergeContext,
                cancellationToken: cancellationToken);

            initiator.Complete();
            await initiatorRun.ConfigureAwait(false);

            Assert.AreEqual(AntiEntropySessionState.Interrupted, initiator.State);
            Assert.AreEqual(0, completions);
        }

        //Phase B: interrupted at Resolving with a fetch outstanding — driven there by a scratch encoder over the
        //peer's items, then wound down before any answer arrives.
        {
            RemoveAwareReconciliationHost initiatorHost = new(shared);
            RemoveAwareReconciliationHost responderHost = new(responderStart);
            using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

            int completions = 0;
            SendReconciliationEnvelopeDelegate<DottedEntry<string>> countingVoid = (envelope, token) =>
            {
                if(envelope.Completion is not null)
                {
                    completions++;
                }

                return ValueTask.CompletedTask;
            };

            Task initiatorRun = initiator.RunAsync(
                countingVoid,
                initiatorHost.ResolveDifference,
                null,
                initiatorHost.ApplyElements,
                applyDrops: initiatorHost.ApplyDrops,
                mergeContext: initiatorHost.MergeContext,
                cancellationToken: cancellationToken);

            await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
            await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForContext(new ReconciliationContext(responderHost.LocalContext)), cancellationToken).ConfigureAwait(false);

            using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
            foreach(ReadOnlyMemory<byte> item in responderHost.Items)
            {
                remote.Add(item.Span);
            }

            int submissions = 0;
            while(initiator.State != AntiEntropySessionState.Resolving)
            {
                int startIndex = remote.ProducedCount;
                ReconciliationSymbol symbol = remote.ProduceNext();
                await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                submissions++;
                Assert.IsLessThan(TriggerCap, submissions, "The initiator never reached Resolving within the submission cap.");
            }

            initiator.Complete();
            await initiatorRun.ConfigureAwait(false);

            Assert.AreEqual(AntiEntropySessionState.Interrupted, initiator.State);
            Assert.AreEqual(0, completions);
        }
    }


    [TestMethod]
    public async Task ACompletedPathFollowUpSessionDropsNothingFalsely()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The no-false-drop probe on the completed/folded path: after one-session convergence (the responder folded
        //the initiator's context on the completion frame), a follow-up session between the same peers must be
        //quiescent and drop no live entry — the fold rode a completed exchange, so no context covers a dot whose
        //entry was never transferred.
        (DottedVersionVectorSet<string> initiatorStart, DottedVersionVectorSet<string> responderStart) = BuildDivergence(DivergenceShape.Mixed);
        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);

        (RemoveAwareReconciliationHost initiatorHost, RemoveAwareReconciliationHost responderHost) = await ReconcileOnceAsync(initiatorStart, responderStart, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, initiatorHost.Current);
        Assert.AreEqual(expected, responderHost.Current);

        //The follow-up session over the converged states transfers nothing and keeps every live entry, with the
        //removed probe still absent — no legitimate entry is falsely dropped.
        (RemoveAwareReconciliationHost followUpInitiator, RemoveAwareReconciliationHost followUpResponder, FrameCensus toResponder, FrameCensus toInitiator) =
            await ReconcileOnceWithCensusAsync(initiatorHost.Current, responderHost.Current, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, toResponder.FetchOrElementsOrDrop);
        Assert.AreEqual(0, toInitiator.FetchOrElementsOrDrop);
        Assert.AreEqual(expected, followUpInitiator.Current);
        Assert.AreEqual(expected, followUpResponder.Current);
        foreach(string value in expected.Values)
        {
            Assert.Contains(value, followUpInitiator.Current.Values);
            Assert.Contains(value, followUpResponder.Current.Values);
        }

        Assert.DoesNotContain(ResurrectionProbe, followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpResponder.Current.Values);
    }


    [TestMethod]
    public async Task AnInterruptedExchangeFoldsNoContextAndCausesNoFalseDropsInAFollowUpSession()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The false-drop scenario the drain-path fold used to cause: the initiator crashes mid-exchange after
        //shipping its causal context, and the host winds the responder down with Complete(). A drain that
        //folded the peer context here would make the responder's context cover the never-transferred entry,
        //and the follow-up session would classify it observed-and-removed and delete it cluster-wide. The
        //drain must fold nothing, report Interrupted, and leave the follow-up session to transfer the entry.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha").Add(R1, "beta");
        DottedVersionVectorSet<string> initiatorFull = shared.Add(R2, "unicorn");
        DottedVersionVectorSet<string> responderStart = shared;

        DottedVersionVectorSet<string> expected = initiatorFull.Merge(responderStart);
        Assert.Contains("unicorn", expected.Values);

        RemoveAwareReconciliationHost responderHost = new(responderStart);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

        int terminalMerges = 0;
        MergeReconciliationContextDelegate countingMerge = async (peerContext, mergeToken) =>
        {
            terminalMerges++;
            await responderHost.MergeContext(peerContext, mergeToken).ConfigureAwait(false);
        };

        Task responderRun = responder.RunAsync(
            DiscardEnvelope,
            null,
            responderHost.ServeFetch,
            responderHost.ApplyElements,
            applyDrops: responderHost.ApplyDrops,
            mergeContext: countingMerge,
            cancellationToken: cancellationToken);

        //The initiator gets as far as its offer and its causal context — which covers the unicorn dot — and
        //then crashes: no symbol, done, fetch, elements, or drop frame ever arrives.
        await responder.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForContext(new ReconciliationContext(initiatorFull.Context.ToState())), cancellationToken).ConfigureAwait(false);
        responder.Complete();
        await responderRun.ConfigureAwait(false);

        //The wind-down is observable as interrupted and folded nothing: the responder's context still does
        //not cover the never-transferred entry.
        Assert.AreEqual(AntiEntropySessionState.Interrupted, responder.State);
        Assert.IsFalse(responder.IsConverged, "An interrupted wind-down never reached the reconciliation path, so it is not converged.");
        Assert.AreEqual(0, terminalMerges);
        Assert.AreEqual(responderStart, responderHost.Current);

        //The follow-up session between the recovered initiator and the untouched responder transfers the
        //entry to both sides; nothing is falsely dropped.
        (RemoveAwareReconciliationHost recoveredInitiator, RemoveAwareReconciliationHost followUpResponder) = await ReconcileOnceAsync(initiatorFull, responderHost.Current, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, recoveredInitiator.Current);
        Assert.AreEqual(expected, followUpResponder.Current);
        Assert.Contains("unicorn", followUpResponder.Current.Values);
    }


    [TestMethod]
    public async Task AnInitiatorInterruptedWithAFetchOutstandingDefersItsLocalDropsAndFoldsNothing()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The initiator-side twin of the drain-fold hazard: the responder observed-and-removed "ghost" (which
        //the initiator still holds) and holds the live "unicorn" (which the initiator lacks), so the decode
        //classifies ghost a local drop and unicorn a fetch. Applying the drop before the answer would fold the
        //responder's full context — covering the never-fetched unicorn dot — and an interruption at Resolving
        //would persist it, making the next session false-drop the live entry. The drops must instead ride to
        //the answer's apply, so an interrupted initiator has folded and dropped nothing.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha");
        DottedVersionVectorSet<string> withGhost = shared.Add(R3, ResurrectionProbe);
        DottedVersionVectorSet<string> initiatorStart = withGhost;
        DottedVersionVectorSet<string> responderStart = withGhost.RemoveValue(ResurrectionProbe).Add(R3, "unicorn");

        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
        Assert.Contains("unicorn", expected.Values);
        Assert.DoesNotContain(ResurrectionProbe, expected.Values);

        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);
        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

        Task initiatorRun = initiator.RunAsync(
            DiscardEnvelope,
            initiatorHost.ResolveDifference,
            null,
            initiatorHost.ApplyElements,
            applyDrops: initiatorHost.ApplyDrops,
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        //Feed the initiator the responder's offer, context, and coded symbols from a scratch encoder over the
        //responder's items; it decodes, sends done and the unicorn fetch into the void, and parks in Resolving
        //with the ghost drop pending — then the host winds it down before any answer arrives.
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForContext(new ReconciliationContext(responderHost.LocalContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderHost.Items)
        {
            remote.Add(item.Span);
        }

        int submissions = 0;
        while(initiator.State != AntiEntropySessionState.Resolving)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            submissions++;
            Assert.IsLessThan(TriggerCap, submissions, "The initiator never reached Resolving within the submission cap.");
        }

        initiator.Complete();
        await initiatorRun.ConfigureAwait(false);

        //Nothing folded and nothing dropped: the local state is untouched and the wind-down is observable.
        Assert.AreEqual(AntiEntropySessionState.Interrupted, initiator.State);
        Assert.IsFalse(initiator.IsConverged, "An initiator wound down with a fetch outstanding never reached the reconciliation path, so it is not converged.");
        Assert.AreEqual(initiatorStart, initiatorHost.Current);
        Assert.Contains(ResurrectionProbe, initiatorHost.Current.Values);

        //The follow-up session converges both sides to the oracle: the unicorn lives (no false drop) and the
        //ghost's deferred drop applies with the completed exchange.
        (RemoveAwareReconciliationHost followUpInitiator, RemoveAwareReconciliationHost followUpResponder) = await ReconcileOnceAsync(initiatorHost.Current, responderStart, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, followUpInitiator.Current);
        Assert.AreEqual(expected, followUpResponder.Current);
        Assert.Contains("unicorn", followUpInitiator.Current.Values);
        Assert.Contains("unicorn", followUpResponder.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpResponder.Current.Values);
    }


    [TestMethod]
    public async Task AFaultingFetchAnswerApplyLeavesTheDeferredDropsUnappliedAndFoldsNothing()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The crash-window law behind the deferral: every drop applier folds the FULL peer context, so on the
        //answer path the entries must apply first — the fold then rides the same hook call as the entries it
        //covers. A faulting elements apply must therefore leave the deferred drops unapplied and nothing
        //folded; a context folded over the never-applied unicorn classifies it observed-and-removed in the
        //next session, a permanent false drop.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha");
        DottedVersionVectorSet<string> withGhost = shared.Add(R3, ResurrectionProbe);
        DottedVersionVectorSet<string> initiatorStart = withGhost;
        DottedVersionVectorSet<string> responderStart = withGhost.RemoveValue(ResurrectionProbe).Add(R3, "unicorn");

        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
        Assert.Contains("unicorn", expected.Values);
        Assert.DoesNotContain(ResurrectionProbe, expected.Values);

        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);
        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

        //The send capture keeps the outbound fetch so the answer can be served back; the elements apply
        //faults before touching the host, and the drop applier counts its invocations.
        List<ReconciliationEnvelope<DottedEntry<string>>> outbound = [];
        int dropApplierCalls = 0;

        Task initiatorRun = initiator.RunAsync(
            (envelope, sendToken) =>
            {
                outbound.Add(envelope);

                return ValueTask.CompletedTask;
            },
            initiatorHost.ResolveDifference,
            null,
            (entries, peerContext, applyToken) => throw new NotSupportedException("The elements apply faulted mid-handler."),
            applyDrops: (dots, peerContext, applyToken) =>
            {
                dropApplierCalls++;

                return initiatorHost.ApplyDrops(dots, peerContext, applyToken);
            },
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForContext(new ReconciliationContext(responderHost.LocalContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderHost.Items)
        {
            remote.Add(item.Span);
        }

        int submissions = 0;
        while(initiator.State != AntiEntropySessionState.Resolving)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            submissions++;
            Assert.IsLessThan(TriggerCap, submissions, "The initiator never reached Resolving within the submission cap.");
        }

        //The initiator parked in Resolving with the ghost drop deferred and the unicorn fetch captured; the
        //answer arrives and its apply faults.
        ReconciliationFetch? fetch = null;
        foreach(ReconciliationEnvelope<DottedEntry<string>> envelope in outbound)
        {
            if(envelope.Fetch is not null)
            {
                fetch = envelope.Fetch;
            }
        }

        Assert.IsNotNull(fetch, "The initiator never sent the unicorn fetch.");
        ImmutableArray<ReconciliationElementEntry<DottedEntry<string>>> answer = [.. responderHost.ServeFetch(fetch.Items)];
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForElements(new ReconciliationElements<DottedEntry<string>>(answer)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => initiatorRun).ConfigureAwait(false);

        //The regression: the fault landed before the drop applier ran, so nothing dropped and nothing folded —
        //the local state is byte-identical and the ghost is still held.
        Assert.AreEqual(0, dropApplierCalls, "A faulting fetch-answer apply must leave the deferred local drops unapplied.");
        Assert.AreEqual(initiatorStart, initiatorHost.Current);
        Assert.Contains(ResurrectionProbe, initiatorHost.Current.Values);

        //The follow-up session converges to the oracle: the unicorn lives (no false drop from the faulted
        //exchange) and the ghost's drop applies with the completed exchange.
        (RemoveAwareReconciliationHost followUpInitiator, RemoveAwareReconciliationHost followUpResponder) = await ReconcileOnceAsync(initiatorHost.Current, responderStart, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, followUpInitiator.Current);
        Assert.AreEqual(expected, followUpResponder.Current);
        Assert.Contains("unicorn", followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpResponder.Current.Values);
    }


    [TestMethod]
    public async Task AFaultingDeferredDropApplyAfterTheAnswerCausesNoFalseDrop()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The elements-first order's own crash window, pinned safe: the answer applies and folds — the fold
        //riding the hook call that carries the entries it covers — and THEN the deferred drop applier faults.
        //The ghost is still held with the peer context folded over it, which is exactly the shape the next
        //session re-classifies as a genuine local drop; nothing here can false-drop live data.
        DottedVersionVectorSet<string> shared = DvvSet().Add(R1, "alpha");
        DottedVersionVectorSet<string> withGhost = shared.Add(R3, ResurrectionProbe);
        DottedVersionVectorSet<string> initiatorStart = withGhost;
        DottedVersionVectorSet<string> responderStart = withGhost.RemoveValue(ResurrectionProbe).Add(R3, "unicorn");

        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
        Assert.Contains("unicorn", expected.Values);
        Assert.DoesNotContain(ResurrectionProbe, expected.Values);

        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);
        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

        List<ReconciliationEnvelope<DottedEntry<string>>> outbound = [];
        bool elementsApplied = false;

        Task initiatorRun = initiator.RunAsync(
            (envelope, sendToken) =>
            {
                outbound.Add(envelope);

                return ValueTask.CompletedTask;
            },
            initiatorHost.ResolveDifference,
            null,
            async (entries, peerContext, applyToken) =>
            {
                ImmutableArray<DotState> surfaced = await initiatorHost.ApplyElements(entries, peerContext, applyToken).ConfigureAwait(false);
                elementsApplied = true;

                return surfaced;
            },
            applyDrops: (dots, peerContext, applyToken) => throw new NotSupportedException("The deferred drop apply faulted mid-handler."),
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForContext(new ReconciliationContext(responderHost.LocalContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderHost.Items)
        {
            remote.Add(item.Span);
        }

        int submissions = 0;
        while(initiator.State != AntiEntropySessionState.Resolving)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            submissions++;
            Assert.IsLessThan(TriggerCap, submissions, "The initiator never reached Resolving within the submission cap.");
        }

        ReconciliationFetch? fetch = null;
        foreach(ReconciliationEnvelope<DottedEntry<string>> envelope in outbound)
        {
            if(envelope.Fetch is not null)
            {
                fetch = envelope.Fetch;
            }
        }

        Assert.IsNotNull(fetch, "The initiator never sent the unicorn fetch.");
        ImmutableArray<ReconciliationElementEntry<DottedEntry<string>>> answer = [.. responderHost.ServeFetch(fetch.Items)];
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForElements(new ReconciliationElements<DottedEntry<string>>(answer)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => initiatorRun).ConfigureAwait(false);

        //The answer landed before the fault: the unicorn is present and the ghost is still held, so the
        //unapplied local drop remains re-classifiable rather than lost.
        Assert.IsTrue(elementsApplied, "The answer's elements must apply before the deferred drops.");
        Assert.Contains("unicorn", initiatorHost.Current.Values);
        Assert.Contains(ResurrectionProbe, initiatorHost.Current.Values);

        //The follow-up session converges to the oracle: the ghost re-classifies as a local drop against the
        //responder's context and the unicorn stays alive — the fault cost one session, never data.
        (RemoveAwareReconciliationHost followUpInitiator, RemoveAwareReconciliationHost followUpResponder) = await ReconcileOnceAsync(initiatorHost.Current, responderStart, cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(expected, followUpInitiator.Current);
        Assert.AreEqual(expected, followUpResponder.Current);
        Assert.Contains("unicorn", followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpInitiator.Current.Values);
        Assert.DoesNotContain(ResurrectionProbe, followUpResponder.Current.Values);
    }


    [TestMethod]
    public async Task ADropDispatchedOnAnInitiatorFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Drops flow only initiator-to-responder: on the ordered channel the responder's fetch answer precedes
        //any drop it could send, so a drop reaching a running initiator is an order-violating peer. Applying
        //it would fold the peer context before the fetch answer, so the dispatch fails closed instead.
        DottedVersionVectorSet<string> snapshot = DvvSet().Add(R1, "alpha").Add(R3, "beta");
        RemoveAwareReconciliationHost initiatorHost = new(snapshot);
        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);

        Task initiatorRun = initiator.RunAsync(
            DiscardEnvelope,
            initiatorHost.ResolveDifference,
            null,
            initiatorHost.ApplyElements,
            applyDrops: initiatorHost.ApplyDrops,
            mergeContext: initiatorHost.MergeContext,
            cancellationToken: cancellationToken);

        DottedEntry<string> entry = snapshot.ToState().Entries[0];
        ReconciliationDrop drop = new([new DotState(entry.Replica, entry.Counter)]);
        await initiator.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForDrop(drop), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARemoveAwareResponderFailsClosedWhenTheDoneSignalArrivesWithoutAPeerContext()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //A nonconforming peer ships offer and done but never its causal context. The responder must fault its
        //run instead of substituting the empty clock and completing as if the remove-aware exchange had
        //happened; the initiator has failed closed on this path since slice 3, and the responder levels with it.
        DottedVersionVectorSet<string> snapshot = DvvSet().Add(R1, "alpha");
        RemoveAwareReconciliationHost responderHost = new(snapshot);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

        Task responderRun = responder.RunAsync(
            DiscardEnvelope,
            null,
            responderHost.ServeFetch,
            responderHost.ApplyElements,
            applyDrops: responderHost.ApplyDrops,
            mergeContext: responderHost.MergeContext,
            cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<DottedEntry<string>>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
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

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

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

        //No fetch, elements, or drop frame crosses in either direction; only offer, context, done, and the new
        //completion frame do.
        Assert.AreEqual(0, toResponder.FetchOrElementsOrDrop);
        Assert.AreEqual(0, toInitiator.FetchOrElementsOrDrop);

        //The completion frame is the one crossing this census now accounts for: the initiator sends exactly one to
        //the responder (carrying zero transfers on this quiescent path), and the responder sends none.
        Assert.AreEqual(1, toResponder.Completion);
        Assert.AreEqual(0, toInitiator.Completion);

        //The initiator's terminal merge still fires on this quiescent path; the responder folds nothing, and
        //with equal snapshots the merged context equals each side's own, so both hosts report it regardless.
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

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, BaseMemoryPool.Shared);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

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

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

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


    //Runs one remove-aware session like ReconcileOnceAsync but wraps each side's send in a FrameCensus, so a
    //caller can assert which payloads crossed — used where the proof is quiescence (no fetch/elements/drop) rather
    //than only the converged values.
    private static async Task<(RemoveAwareReconciliationHost Initiator, RemoveAwareReconciliationHost Responder, FrameCensus ToResponder, FrameCensus ToInitiator)> ReconcileOnceWithCensusAsync(
        DottedVersionVectorSet<string> initiatorStart,
        DottedVersionVectorSet<string> responderStart,
        CancellationToken cancellationToken)
    {
        RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
        RemoveAwareReconciliationHost responderHost = new(responderStart);

        using AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: initiatorHost.LocalContext);
        using AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderHost.Items, DefaultBatchSize, BaseMemoryPool.Shared, localContext: responderHost.LocalContext);

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

        return (initiatorHost, responderHost, toResponder, toInitiator);
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


    private static ValueTask DiscardEnvelope(ReconciliationEnvelope<DottedEntry<string>> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
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

        public int Completion { get; private set; }


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

                if(envelope.Completion is not null)
                {
                    Completion++;
                }

                return ForwardTo(peer, envelope, cancellationToken);
            };
        }
    }


}
