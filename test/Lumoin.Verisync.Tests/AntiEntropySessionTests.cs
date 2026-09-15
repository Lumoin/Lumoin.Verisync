using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// In-memory coverage of the anti-entropy session runner: a pair of sessions whose send delegates route
/// envelopes to the peer's <see cref="AntiEntropySession{TElement}.SubmitAsync"/>, paced by the host calling
/// the responder's <see cref="AntiEntropySession{TElement}.TriggerBatchAsync"/> until the initiator reports
/// <see cref="AntiEntropySessionState.Completed"/>. Covers convergence, quiescence, offer mismatch, role and
/// gap violations, constructor and run validation, fetch-coverage enforcement, missing-apply faults, straggler
/// tolerance, the submit shape guard, snapshot pinning, the resolution record's validation and equality, the
/// <see cref="AntiEntropySessionState.Interrupted"/> wind-down report, the add-only rejection of the
/// remove-aware context and drop frames, the add-only fail-closed on resolver-supplied local drops, and the
/// session completion frame's normative guard order — add-only (a), role (b), phase (c), and transfer-count
/// (d) rejections, each constructed to be non-vacuous, plus the emergent no-frame-after-completion contract.
/// </summary>
[TestClass]
internal sealed class AntiEntropySessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int BatchSize = 4;

    /// <summary>
    /// A remove-aware session needs a non-null local context; the empty clock is the simplest one that turns the
    /// completion-frame dispatch arms on for the guard tests below.
    /// </summary>
    private static VectorClockState EmptyContext { get; } = VectorClock.Empty.ToState();

    /// <summary>
    /// A representative peer context the guard tests feed a responder before its done signal, so the fold seam has
    /// a held context to draw on when a verified completion lands.
    /// </summary>
    private static VectorClockState SamplePeerContext { get; } = VectorClock.Empty.Increment(Replica(1)).ToState();

    private static ServeReconciliationFetchDelegate<string> ServeNothing { get; } = _ => [];

    private static ApplyReconciliationElementsDelegate<string> ApplyNoElements { get; } = (_, _, _) => new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);

    private static ApplyReconciliationDropsDelegate<string> ApplyNoDrops { get; } = (_, _, _) => ValueTask.CompletedTask;

    private static MergeReconciliationContextDelegate NoMerge { get; } = (_, _) => ValueTask.CompletedTask;

    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);

    private static string[] ExpectedConverged { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task FullConvergenceExchangesTheThreeDigestDifferenceAndBothSetsReachAllSix()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
        Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
        HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

        //The initiator partitions the decoded difference: digests it lacks become a fetch, digests it holds
        //in surplus become pushed entries; the responder lookups serve fetches and both sides apply received
        //elements under their own replica id.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
        {
            ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
            ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
            foreach(ReadOnlyMemory<byte> item in decoded)
            {
                string hex = Convert.ToHexString(item.Span);
                if(initiatorHexes.Contains(hex))
                {
                    push.Add(new ReconciliationElementEntry<string>(item, initiatorDirectory[hex]));
                }
                else
                {
                    fetch.Add(item);
                }
            }

            return new ReconciliationDifferenceResolution<string>(fetch.ToImmutable(), push.ToImmutable());
        };

        ServeReconciliationFetchDelegate<string> serve = items =>
            [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];

        OrSet<string> initiatorResult = initiatorSet;
        ApplyReconciliationElementsDelegate<string> applyToInitiator = (entries, _, ct) =>
        {
            foreach(ReconciliationElementEntry<string> entry in entries)
            {
                initiatorResult = initiatorResult.Add(entry.Element, R2);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        OrSet<string> responderResult = responderSet;
        ApplyReconciliationElementsDelegate<string> applyToResponder = (entries, _, ct) =>
        {
            foreach(ReconciliationElementEntry<string> entry in entries)
            {
                responderResult = responderResult.Add(entry.Element, R3);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyToInitiator, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, applyToResponder, cancellationToken: cancellationToken);

        await DriveInitiatorToCompletionAsync(initiatorRun, responder, ContentHashContract, initiatorItems, responderItems, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedConverged, Sorted(initiatorResult));
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(responderResult));
        Assert.HasCount(3, initiator.DecodedItems);
        Assert.IsTrue(initiatorRun.IsCompletedSuccessfully);
        Assert.IsTrue(responderRun.IsCompletedSuccessfully);
    }


    [TestMethod]
    public async Task QuiescenceCompletesAfterTheFirstBatchWithNoFetchOrElementsAcrossTheWire()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2, A3];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        int resolveInvocations = 0;
        int resolveDecodedCount = -1;
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
        {
            resolveInvocations++;
            resolveDecodedCount = decoded.Count;

            return ReconciliationDifferenceResolution<string>.Empty;
        };

        ServeReconciliationFetchDelegate<string> serve = _ => [];

        int fetchOrElementsToResponder = 0;
        SendReconciliationEnvelopeDelegate<string> sendToResponder = (envelope, token) =>
        {
            if(envelope.Fetch is not null || envelope.Elements is not null)
            {
                fetchOrElementsToResponder++;
            }

            return ForwardTo(responder, envelope, token);
        };

        int fetchOrElementsToInitiator = 0;
        SendReconciliationEnvelopeDelegate<string> sendToInitiator = (envelope, token) =>
        {
            if(envelope.Fetch is not null || envelope.Elements is not null)
            {
                fetchOrElementsToInitiator++;
            }

            return ForwardTo(initiator, envelope, token);
        };

        Task initiatorRun = initiator.RunAsync(sendToResponder, resolve, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(sendToInitiator, null, serve, null, cancellationToken: cancellationToken);

        await DriveInitiatorToCompletionAsync(initiatorRun, responder, StructuralContract, items, items, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.IsEmpty(initiator.DecodedItems);
        Assert.AreEqual(1, resolveInvocations);
        Assert.AreEqual(0, resolveDecodedCount);
        Assert.AreEqual(0, fetchOrElementsToResponder);
        Assert.AreEqual(0, fetchOrElementsToInitiator);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
        Assert.AreEqual(AntiEntropySessionState.Completed, responder.State);
    }


    [TestMethod]
    public async Task OfferMismatchFaultsTheInitiatorRunWithInvalidOperation()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReconciliationContract otherContract = new(ReconciliationItemDomain.Structural, 8, 8, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, otherContract, items, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        //The peer's mismatched offer is already enqueued by the responder's opening send, so completing the channel
        //now drains a regressed guard to a normal return instead of a park, surfacing the fault without the timeout.
        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("The peer's offer does not match the local contract.", fault.Message);

        responder.Complete();
        await SwallowAsync(responderRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task SymbolsToAResponderFaultsItsRunAndTriggerOnAnInitiatorThrowsSynchronously()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        //A trigger addressed to an initiator is caller error and throws synchronously, before any run begins.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiator.TriggerBatchAsync(cancellationToken).AsTask()).ConfigureAwait(false);

        ReconciliationSymbolBatch batch = new(0, [new ReconciliationSymbol(A1, new byte[8])]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(batch), cancellationToken).ConfigureAwait(false);

        //The responder is still Pinning (never received an inbound offer), so only the role guard can fire; a
        //dropped role guard would fall through to the phase guard instead, throwing the same type with a different
        //message -- asserting the message pins which guard actually fired.
        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.Contains("Only an initiator absorbs symbol batches", fault.Message);
    }


    [TestMethod]
    public async Task AGapInTheSymbolStreamFaultsTheInitiator()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, null, cancellationToken: cancellationToken);

        //The decoder stands at AbsorbedCount zero, so a batch starting one ahead at index one is a gap.
        ReconciliationSymbolBatch forged = new(1, [new ReconciliationSymbol(A1, new byte[8])]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(forged), cancellationToken).ConfigureAwait(false);
        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("A symbol batch must start at the decoder's absorbed count for gap-free, in-order streaming.", fault.Message);

        responder.Complete();
    }


    [TestMethod]
    public void ConstructorValidationRejectsBadArguments()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AntiEntropySession<string>((AntiEntropyRole)0, StructuralContract, items, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentNullException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, null!, items, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentNullException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, null!, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, items, 0, BaseMemoryPool.Shared));

        //A too-narrow item is rejected by the session's own width check (ParamName "items"), independent of the
        //encoder's own width check on Add (ParamName "item").
        ReadOnlyMemory<byte>[] narrowItem = [new byte[4]];
        ArgumentException wrongWidthFault = Assert.ThrowsExactly<ArgumentException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, narrowItem, BaseMemoryPool.Shared));
        Assert.AreEqual("items", wrongWidthFault.ParamName);

        //The null pool is paired with a too-narrow item so that the session's own pool guard is the only guard that
        //can name "pool"; the width check names "items".
        ArgumentNullException nullPoolFault = Assert.ThrowsExactly<ArgumentNullException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, narrowItem, null!));
        Assert.AreEqual("pool", nullPoolFault.ParamName);

        ReadOnlyMemory<byte>[] duplicates = [A1, A1.ToArray()];
        Assert.ThrowsExactly<ArgumentException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, duplicates, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public async Task RunValidationRejectsMissingRoleDelegatesAndASecondRun()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];

        using AntiEntropySession<string> missingResolve = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => missingResolve.RunAsync(Discard, null, null, null, cancellationToken: cancellationToken)).ConfigureAwait(false);

        using AntiEntropySession<string> missingServe = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => missingServe.RunAsync(Discard, null, null, null, cancellationToken: cancellationToken)).ConfigureAwait(false);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serve = _ => [];
        Task first = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken)).ConfigureAwait(false);

        responder.Complete();
        await SwallowAsync(first).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AFetchAnswerMissingAnEntryFaultsTheResponder()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        //The guard under test lives on a lone responder: an offer then a done signal park it in resolving, and a
        //fetch answer with no entries at all trips the count check with no initiator in the loop.
        ServeReconciliationFetchDelegate<string> serveTooFew = _ => [];
        Task responderRun = responder.RunAsync(Discard, null, serveTooFew, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> requestedItem = SHA256.HashData(Encoding.UTF8.GetBytes("zeta"));
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(new ReconciliationFetch([requestedItem])), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A fetch answer must cover exactly the requested items.", fault.Message);
    }


    [TestMethod]
    public async Task ElementsWithoutAnApplyHookFaultsTheReceivingSide()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> responderSet = ancestor;

        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        //The guard under test lives on a lone add-only responder: an offer then a done signal park it in resolving,
        //and a pushed elements frame with no apply hook wired trips the missing-hook guard with no initiator in the loop.
        ServeReconciliationFetchDelegate<string> serve = _ => [];
        Task responderRun = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> pushedItem = SHA256.HashData(Encoding.UTF8.GetBytes("delta"));
        ReconciliationElements<string> pushed = new([new ReconciliationElementEntry<string>(pushedItem, "delta")]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForElements(pushed), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("An elements message arrived without an apply hook.", fault.Message);
    }


    [TestMethod]
    public async Task StragglerSymbolsAfterResolvingAreIgnoredWithoutFaultingOrAbsorbing()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> initiatorSet = ancestor;
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, BaseMemoryPool.Shared);

        //The initiator lacks zeta, so after decoding it fetches and parks in Resolving with an outstanding answer.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            new ReconciliationDifferenceResolution<string>([.. decoded], []);
        ApplyReconciliationElementsDelegate<string> applyNothing = (_, _, ct) => new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, applyNothing, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);

        //A local mirror reproduces the initiator's decoder over the same items and the peer's coded symbols, so it
        //names the exact prefix that completes the decode and parks the initiator in Resolving with its fetch
        //outstanding; the mirror's decoded count is the count a later straggler must leave untouched.
        using ReconciliationEncoder local = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(ContentHashContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        int absorbedAtResolving = mirror.DecodedItems.Count;

        //A well-formed straggler continuing the stream after the decode completed must be ignored, not absorbed:
        //ordered delivery hands it to the initiator while it is already Resolving, ahead of the fetch answer below.
        int stragglerIndex = remote.ProducedCount;
        ReconciliationSymbol stragglerSymbol = remote.ProduceNext();
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(stragglerIndex, [stragglerSymbol])), cancellationToken).ConfigureAwait(false);

        //The outstanding fetch is answered directly, so the initiator applies it and completes; the straggler that
        //preceded it in the channel left the decoded difference unchanged.
        ReadOnlyMemory<byte> zetaItem = SHA256.HashData(Encoding.UTF8.GetBytes("zeta"));
        ReconciliationElements<string> answer = new([new ReconciliationElementEntry<string>(zetaItem, "zeta")]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForElements(answer), cancellationToken).ConfigureAwait(false);

        await initiatorRun.ConfigureAwait(false);

        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
        Assert.HasCount(absorbedAtResolving, initiator.DecodedItems);
    }


    [TestMethod]
    public async Task SubmitAsyncRejectsEnvelopesWithoutExactlyOnePayload()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> session = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);

        ReconciliationEnvelope<string> empty = new(null, null, null, null, null, null, null, null);
        ReconciliationOffer offer = ReconciliationOffer.FromContract(StructuralContract);
        ReconciliationDone done = new(1);
        ReconciliationEnvelope<string> two = new(offer, null, done, null, null, null, null, null);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.SubmitAsync(empty, cancellationToken).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.SubmitAsync(two, cancellationToken).AsTask()).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task TheSnapshotIsPinnedAgainstListAndBufferMutationAfterConstruction()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        //Mutable list over mutable buffers: after the sessions copy the snapshot, mutating both the list and
        //the underlying arrays must not change the run outcome.
        byte[][] initiatorArrays = [.. ProjectHashes(initiatorSet).Select(item => item.ToArray())];
        byte[][] responderArrays = [.. ProjectHashes(responderSet).Select(item => item.ToArray())];
        List<ReadOnlyMemory<byte>> initiatorBuffers = [.. initiatorArrays.Select(array => (ReadOnlyMemory<byte>)array)];
        List<ReadOnlyMemory<byte>> responderBuffers = [.. responderArrays.Select(array => (ReadOnlyMemory<byte>)array)];

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorBuffers, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderBuffers, BaseMemoryPool.Shared);

        //Corrupt every backing array and empty both lists; a snapshot that copied its bytes is unaffected.
        foreach(byte[] array in initiatorArrays)
        {
            Array.Clear(array);
        }

        foreach(byte[] array in responderArrays)
        {
            Array.Clear(array);
        }

        initiatorBuffers.Clear();
        responderBuffers.Clear();

        Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
        Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
        HashSet<string> initiatorHexes = [.. ProjectHashes(initiatorSet).Select(item => Convert.ToHexString(item.Span))];

        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
        {
            ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
            ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
            foreach(ReadOnlyMemory<byte> item in decoded)
            {
                string hex = Convert.ToHexString(item.Span);
                if(initiatorHexes.Contains(hex))
                {
                    push.Add(new ReconciliationElementEntry<string>(item, initiatorDirectory[hex]));
                }
                else
                {
                    fetch.Add(item);
                }
            }

            return new ReconciliationDifferenceResolution<string>(fetch.ToImmutable(), push.ToImmutable());
        };

        ServeReconciliationFetchDelegate<string> serve = items =>
            [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];

        OrSet<string> initiatorResult = initiatorSet;
        ApplyReconciliationElementsDelegate<string> applyToInitiator = (entries, _, ct) =>
        {
            foreach(ReconciliationElementEntry<string> entry in entries)
            {
                initiatorResult = initiatorResult.Add(entry.Element, R2);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        OrSet<string> responderResult = responderSet;
        ApplyReconciliationElementsDelegate<string> applyToResponder = (entries, _, ct) =>
        {
            foreach(ReconciliationElementEntry<string> entry in entries)
            {
                responderResult = responderResult.Add(entry.Element, R3);
            }

            return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
        };

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyToInitiator, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, applyToResponder, cancellationToken: cancellationToken);

        await DriveInitiatorToCompletionAsync(initiatorRun, responder, ContentHashContract, ProjectHashes(initiatorSet), ProjectHashes(responderSet), cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedConverged, Sorted(initiatorResult));
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(responderResult));
        Assert.HasCount(3, initiator.DecodedItems);
    }


    [TestMethod]
    public void ResolutionRecordValidatesAndComparesByContent()
    {
        ImmutableArray<ReadOnlyMemory<byte>> defaultFetch = default;
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDifferenceResolution<string>(defaultFetch, []));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDifferenceResolution<string>([], default));

        ReadOnlyMemory<byte> emptyItem = ReadOnlyMemory<byte>.Empty;
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDifferenceResolution<string>([emptyItem], []));

        ReadOnlyMemory<byte> a1 = A1;
        ReadOnlyMemory<byte> a1Copy = A1.ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDifferenceResolution<string>([a1, a1Copy], []));

        ReadOnlyMemory<byte> wide = A2;
        byte[] narrowBytes = [0x01, 0x02, 0x03, 0x04];
        ReadOnlyMemory<byte> narrow = narrowBytes;
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationDifferenceResolution<string>([wide, narrow], []));

        Assert.IsEmpty(ReconciliationDifferenceResolution<string>.Empty.Fetch);
        Assert.IsEmpty(ReconciliationDifferenceResolution<string>.Empty.Push);

        //Equal contents from independent buffers compare equal with equal hash codes.
        ReadOnlyMemory<byte> left = A1;
        ReadOnlyMemory<byte> right = A1.ToArray();
        ReconciliationDifferenceResolution<string> first = new([left], []);
        ReconciliationDifferenceResolution<string> second = new([right], []);
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }


    [TestMethod]
    public async Task AWindDownBeforeTheExchangeFinishesReportsInterrupted()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //An initiator whose peer never answers and a responder that never receives an offer are wound down by
        //the host: both loops return normally, and the terminal state distinguishes the abandoned exchange
        //from a completed one instead of reporting Completed for both.
        ReadOnlyMemory<byte>[] items = [A1, A2];

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);
        initiator.Complete();
        await initiatorRun.ConfigureAwait(false);

        Assert.AreEqual(AntiEntropySessionState.Interrupted, initiator.State);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serve = _ => [];
        Task responderRun = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);
        responder.Complete();
        await responderRun.ConfigureAwait(false);

        Assert.AreEqual(AntiEntropySessionState.Interrupted, responder.State);
    }


    [TestMethod]
    public async Task AddOnlySessionsRejectContextAndDropFrames()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The remove-aware dispatch arms are gated on the session's own mode: an add-only session facing a
        //remove-aware peer must fail closed on the context and drop frames rather than fold or drop anything.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        using AntiEntropySession<string> contextTarget = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        Task contextRun = contextTarget.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);
        ReconciliationContext context = new(VectorClock.Empty.ToState());
        await contextTarget.SubmitAsync(ReconciliationEnvelope<string>.ForContext(context), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => contextRun).ConfigureAwait(false);

        using AntiEntropySession<string> dropTarget = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        Task dropRun = dropTarget.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);
        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDrop drop = new([new DotState(replica, 1)]);
        await dropTarget.SubmitAsync(ReconciliationEnvelope<string>.ForDrop(drop), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => dropRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AnAddOnlySessionHandedLocalDropsFailsClosedInsteadOfDereferencingAMissingDropPath()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> initiatorSet = ancestor;
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, BaseMemoryPool.Shared);

        //An add-only session carries no local context, no drop applier, and no terminal merge, so a resolver that
        //hands it local drops has no honest path to apply them. On decode completion it must fail closed with
        //InvalidOperationException at that dispatch — the earliest honest point, since the drops arrive at
        //resolution time — rather than dereference the null applier, mirroring the add-only frame rejections above.
        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDifferenceResolution<string> withDrops = new([], [], [new DotState(replica, 1)]);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => withDrops;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);

        //A local mirror reproduces the initiator's decoder over the same items and the peer's coded symbols, so it
        //names the exact prefix that completes the decode; the ordered channel then dispatches the drop-bearing
        //resolution the moment the last symbol lands.
        using ReconciliationEncoder local = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(ContentHashContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("An add-only session carries no drop path; a difference resolver must not return local drops for it.", fault.Message);
    }


    [TestMethod]
    public async Task ACompletionFrameOnAnInitiatorFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Completion travels initiator-to-responder only. A remove-aware initiator driven to Resolving satisfies
        //the add-only (a) and phase (c) guards, so the role guard (b) is the one that fails closed — non-vacuously,
        //since guard (c) at Resolving cannot mask it.
        ReadOnlyMemory<byte>[] initiatorItems = [A1];
        ReadOnlyMemory<byte>[] peerItems = [A1, A2];

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, initiatorItems, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        //Fetching everything it decodes parks the initiator in Resolving with a fetch outstanding for the surplus item.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) => new ReconciliationDifferenceResolution<string>([.. decoded], []);

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in peerItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationEncoder local = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(StructuralContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACompletionFrameCountMismatchFailsClosedWithoutFolding()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Transfer-count guard (d): zero transfer frames were delivered, so a completion claiming one is a
        //cardinality mismatch — loss, truncation, or forgery. It fails closed before any fold, so the recording
        //merge never runs and the responder's context stays at its session start.
        int mergeCalls = 0;
        MergeReconciliationContextDelegate recordingMerge = (_, _) =>
        {
            mergeCalls++;

            return ValueTask.CompletedTask;
        };

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: recordingMerge, cancellationToken: cancellationToken);

        //Lone responder to Resolving via the envelope feed: offer, context, done — no transfer frame between.
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(1)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual(0, mergeCalls);
    }


    [TestMethod]
    public async Task ACompletionFrameBeforeTheDoneSignalFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Phase guard (c): a completion is legal only while Resolving. A remove-aware responder still reconciling
        //(before the done signal) satisfies the add-only (a) and role (b) guards, so guard (c) is the one that
        //fires. The context frame is delivered first, deliberately: with the peer context held and the count
        //matching, dropping or weakening the phase guard would fold and complete instead of throwing from a
        //missing peer context — the same exception type — so delivering the context isolates the phase guard.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AnAddOnlySessionRejectsTheCompletionFrame()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Add-only guard (a): an add-only session carries no context to fold, so it rejects the completion frame
        //even at Resolving — where the role (b) and phase (c) guards would both pass — so the rejection is non-vacuous.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, null, cancellationToken: cancellationToken);

        //An add-only responder reaches Resolving on offer then done, with no context in between.
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AFrameAfterTheCompletionFrameFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //The emergent guard (e): a verified completion folds and lands the responder terminal, but it keeps
        //consuming, so any later frame fails closed through the existing phase guards — here a drop, legal only
        //while Resolving. This pins the no-frame-after-completion contract with no new guard code.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        //Zero transfers delivered and zero claimed: the completion passes every guard, folds, and completes the responder.
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);

        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDrop drop = new([new DotState(replica, 1)]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDrop(drop), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A drop is legal only while resolving.", fault.Message);
    }


    [TestMethod]
    public async Task ADuplicateCompletionFrameFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //A duplicate completion trips the same phase guard (c): the first completion already left Resolving for the
        //terminal state, so a second completion is no longer legal.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
    }


    /// <summary>Pins that an offer arriving after the contract is already pinned fails closed.</summary>
    [TestMethod]
    public async Task ASecondOfferAfterPinningFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("An offer is legal only while pinning the contract.", fault.Message);
    }


    /// <summary>A fetch on a responder that is still pinning is refused by the phase guard, identified by its message.</summary>
    [TestMethod]
    public async Task AFetchBeforeResolvingFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task responderRun = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);

        ReconciliationFetch fetch = new([A1]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(fetch), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A fetch is legal only while resolving.", fault.Message);
    }


    /// <summary>Pins that only a remove-aware responder, not an initiator, eagerly requires an elements applier.</summary>
    [TestMethod]
    public async Task RunAsyncRejectsARemoveAwareResponderMissingTheElementsApplier()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        //A throwing send makes the eager validation observable before any dispatch: the guard must reject the run
        //before the offer is sent, so a lost guard would surface the send's exception in place of the argument fault.
        SendReconciliationEnvelopeDelegate<string> refuseSend = (_, _) => throw new NotSupportedException("The eager validation must reject the run before any send.");

        ArgumentNullException fault = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => responder.RunAsync(refuseSend, null, serve, applyElements: null, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("applyElements", fault.ParamName);
    }


    /// <summary>Pins the role guard for a drop specifically, with the phase already legal (Resolving).</summary>
    [TestMethod]
    public async Task ADropOnAnInitiatorParkedInResolvingFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] initiatorItems = [A1];
        ReadOnlyMemory<byte>[] peerItems = [A1, A2];

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, initiatorItems, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) => new ReconciliationDifferenceResolution<string>([.. decoded], []);

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in peerItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationEncoder local = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(StructuralContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDrop drop = new([new DotState(replica, 1)]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForDrop(drop), cancellationToken).ConfigureAwait(false);
        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("Only a responder applies a received drop; an initiator's exchange ends with its fetch answer.", fault.Message);
    }


    /// <summary>Pins the fail-closed guard for a remove-aware decode completing before the peer's context arrives.</summary>
    [TestMethod]
    public async Task ARemoveAwareDecodeCompletingWithoutThePeerContextFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        //Equal sets so the very first absorbed symbol completes the decode, deliberately without ever submitting a
        //context frame first.
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            remote.Add(item.Span);
        }

        ReconciliationSymbol symbol = remote.ProduceNext();
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(0, [symbol])), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.Contains("before the peer's causal context arrived", fault.Message);
    }


    /// <summary>
    /// The three-argument constructor rejects a default local-drop array exactly as the two-argument overload
    /// rejects a default fetch or push array, naming the local-drop parameter.
    /// </summary>
    [TestMethod]
    public void ResolutionConstructorRejectsDefaultLocalDrops()
    {
        ImmutableArray<DotState> defaultLocalDrops = default;

        ArgumentException thrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], [], defaultLocalDrops));

        Assert.AreEqual("localDrops", thrown.ParamName);
    }


    /// <summary>Pins that RunAsync rejects a null send delegate before any dispatch begins.</summary>
    [TestMethod]
    public async Task RunAsyncRejectsANullSendDelegate()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => initiator.RunAsync(null!, resolve, null, null, cancellationToken: TestContext.CancellationToken)).ConfigureAwait(false);
    }


    /// <summary>
    /// The local-drop hash aggregates every dot's contribution with a reversible, bit-mixing combinator: two
    /// different, large local-drop sets hash differently instead of collapsing toward a saturated all-ones
    /// aggregate the way a bitwise OR accumulation would.
    /// </summary>
    [TestMethod]
    public void HashCodeAggregatesLocalDropsByXorNotByAnAbsorbingOperator()
    {
        ReconciliationDifferenceResolution<string> first = new([], [], ManyDistinctLocalDrops(0));
        ReconciliationDifferenceResolution<string> second = new([], [], ManyDistinctLocalDrops(1));

        Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
    }


    /// <summary>Builds forty-eight distinct local-drop dots whose replica bytes are offset by <paramref name="seedOffset"/>, so two calls with different offsets yield disjoint sets.</summary>
    private static ImmutableArray<DotState> ManyDistinctLocalDrops(byte seedOffset)
    {
        ImmutableArray<DotState>.Builder drops = ImmutableArray.CreateBuilder<DotState>(48);
        for(int i = 0; i < 48; i++)
        {
            byte[] replicaBytes = new byte[ReplicaId.Size];
            replicaBytes[0] = (byte)(i + seedOffset);
            replicaBytes[1] = (byte)((i * 7) + seedOffset);
            drops.Add(new DotState([.. replicaBytes], i + 1));
        }

        return drops.MoveToImmutable();
    }


    /// <summary>Pins the duplicate-context guard specifically: the phase is still legal, so only this guard can fire.</summary>
    [TestMethod]
    public async Task ASecondCausalContextWhileStillReconcilingFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A causal context is exchanged once; a second context is not legal.", fault.Message);
    }


    /// <summary>
    /// Two local-drop dots that share a counter but carry different replicas are not duplicates, so the
    /// constructor accepts both instead of rejecting the second as a repeat of the first.
    /// </summary>
    [TestMethod]
    public void ResolutionConstructorAcceptsDistinctLocalDropsSharingACounter()
    {
        ImmutableArray<byte> replicaA = [.. R1.ToArray()];
        ImmutableArray<byte> replicaB = [.. R2.ToArray()];
        ImmutableArray<DotState> distinctDrops = [new DotState(replicaA, 1), new DotState(replicaB, 1)];

        ReconciliationDifferenceResolution<string> resolution = new([], [], distinctDrops);

        Assert.HasCount(2, resolution.LocalDrops);
    }


    /// <summary>Local drops deferred behind an outstanding fetch apply together with the fetch answer; the terminal merge must not run again.</summary>
    [TestMethod]
    public async Task DeferredLocalDropsAppliedWithTheFetchAnswerFoldTheContextExactlyOnce()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] initiatorItems = [A1];
        ReadOnlyMemory<byte>[] peerItems = [A1, A2];
        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];

        int mergeCalls = 0;
        MergeReconciliationContextDelegate recordingMerge = (_, _) =>
        {
            mergeCalls++;

            return ValueTask.CompletedTask;
        };

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, initiatorItems, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            new ReconciliationDifferenceResolution<string>([.. decoded], [], [new DotState(replica, 1)]);

        bool fetchSent = false;
        SendReconciliationEnvelopeDelegate<string> send = (envelope, token) =>
        {
            if(envelope.Fetch is not null)
            {
                fetchSent = true;
            }

            return ValueTask.CompletedTask;
        };

        Task initiatorRun = initiator.RunAsync(send, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: recordingMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in peerItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationEncoder local = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(StructuralContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        ReconciliationElements<string> answer = new([new ReconciliationElementEntry<string>(A2, "peer-item")]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForElements(answer), cancellationToken).ConfigureAwait(false);

        await initiatorRun.ConfigureAwait(false);

        Assert.IsTrue(fetchSent);
        Assert.AreEqual(0, mergeCalls);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
    }


    /// <summary>Pins the phase guard for the done signal: the role guard cannot fire since Role is Responder.</summary>
    [TestMethod]
    public async Task ADoneSignalBeforeReconcilingFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task responderRun = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A done signal is legal only while reconciling.", fault.Message);
    }


    /// <summary>Pins the count-mismatch guard: every requested item is still found among the served entries.</summary>
    [TestMethod]
    public async Task AFetchAnswerWithASurplusEntryFaultsTheResponder()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
        ReadOnlyMemory<byte> surplusItem = SHA256.HashData(Encoding.UTF8.GetBytes("surplus"));
        ServeReconciliationFetchDelegate<string> serveSurplus = items =>
        {
            List<ReconciliationElementEntry<string>> served = [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];
            served.Add(new ReconciliationElementEntry<string>(surplusItem, "surplus"));

            return served;
        };

        //The guard under test lives on a lone responder: an offer then a done signal park it in resolving, and a
        //fetch answer with one entry too many trips the count check with no initiator in the loop.
        Task responderRun = responder.RunAsync(Discard, null, serveSurplus, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> requestedItem = SHA256.HashData(Encoding.UTF8.GetBytes("zeta"));
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(new ReconciliationFetch([requestedItem])), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A fetch answer must cover exactly the requested items.", fault.Message);
    }


    /// <summary>A done signal on an initiator is refused by the role guard, identified by its message.</summary>
    [TestMethod]
    public async Task ADoneSignalOnAnInitiatorFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("Only a responder receives the done signal.", fault.Message);
    }


    /// <summary>
    /// Two distinct instances holding the same number of fetch items but different item bytes are unequal; the
    /// reference-identity fast path must never fire for a merely similarly-shaped, non-identical instance.
    /// </summary>
    [TestMethod]
    public void EqualsIsFalseForDistinctInstancesWithDifferingFetchContent()
    {
        ReconciliationDifferenceResolution<string> first = new([A1], []);
        ReconciliationDifferenceResolution<string> second = new([A2], []);

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>Local drops applied with no outstanding fetch fold the context inline; the terminal merge must not run again.</summary>
    [TestMethod]
    public async Task LocalDropsAppliedWithNoFetchOutstandingFoldTheContextExactlyOnce()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDifferenceResolution<string> withLocalDropsOnly = new([], [], [new DotState(replica, 1)]);

        int mergeCalls = 0;
        MergeReconciliationContextDelegate recordingMerge = (_, _) =>
        {
            mergeCalls++;

            return ValueTask.CompletedTask;
        };

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => withLocalDropsOnly;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: recordingMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            remote.Add(item.Span);
        }

        ReconciliationSymbol symbol = remote.ProduceNext();
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(0, [symbol])), cancellationToken).ConfigureAwait(false);

        await initiatorRun.ConfigureAwait(false);

        Assert.AreEqual(0, mergeCalls);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
    }


    /// <summary>
    /// A local-drop dot is identified by its replica and its counter together; two dots that share only the
    /// replica, with different counters, are distinct, so resolutions built around them are unequal.
    /// </summary>
    [TestMethod]
    public void EqualsTreatsLocalDropsSharingOnlyAReplicaAsDistinct()
    {
        ImmutableArray<byte> replica = [.. R1.ToArray()];
        ReconciliationDifferenceResolution<string> first = new([], [], [new DotState(replica, 1)]);
        ReconciliationDifferenceResolution<string> second = new([], [], [new DotState(replica, 2)]);

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>Pins that a fetch server returning null fails the fetch closed.</summary>
    [TestMethod]
    public async Task AFetchServerReturningNullFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serveNull = _ => null!;

        Task responderRun = responder.RunAsync(Discard, null, serveNull, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        ReconciliationFetch fetch = new([A1]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(fetch), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
    }


    /// <summary>Pins the eager remove-aware validation that a drop applier is required, isolated from the sibling elements-applier guard by asserting ParamName.</summary>
    [TestMethod]
    public async Task RunAsyncRejectsARemoveAwareSessionMissingTheDropApplier()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        //The drop-applier guard is identified by its parameter name because the sibling applier guards throw the same type.
        ArgumentNullException fault = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => responder.RunAsync(Discard, null, serve, null, applyDrops: null, mergeContext: NoMerge, cancellationToken: TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("applyDrops", fault.ParamName);
    }


    /// <summary>
    /// The hash code is sensitive to the fetch items' bytes and to the push entries, not only to their counts:
    /// two resolutions that differ solely in one fetch item, or solely in one push entry, hash differently.
    /// </summary>
    [TestMethod]
    public void HashCodeIncludesEachFetchItemAndEachPushEntry()
    {
        ReconciliationDifferenceResolution<string> firstFetch = new([A1], []);
        ReconciliationDifferenceResolution<string> secondFetch = new([A2], []);

        Assert.AreNotEqual(firstFetch.GetHashCode(), secondFetch.GetHashCode());

        ReconciliationElementEntry<string> alpha = new(A1, "alpha");
        ReconciliationElementEntry<string> beta = new(A2, "beta");
        ReconciliationDifferenceResolution<string> firstPush = new([], [alpha]);
        ReconciliationDifferenceResolution<string> secondPush = new([], [beta]);

        Assert.AreNotEqual(firstPush.GetHashCode(), secondPush.GetHashCode());
    }


    /// <summary>
    /// Equals reports false against a null operand and true against itself by reference, ahead of any content
    /// comparison.
    /// </summary>
    [TestMethod]
    public void EqualsReturnsFalseForNullAndTrueForSameReference()
    {
        ReconciliationDifferenceResolution<string> resolution = new([A1], []);

        Assert.IsFalse(resolution.Equals(NullResolution()));
        Assert.IsTrue(resolution.Equals(resolution));
    }


    /// <summary>Returns a null resolution from an opaque helper so the null comparison is not folded at compile time.</summary>
    private static ReconciliationDifferenceResolution<string>? NullResolution() => null;


    /// <summary>Pins that a difference resolver returning null fails the decode closed.</summary>
    [TestMethod]
    public async Task ADifferenceResolverReturningNullFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => null!;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            remote.Add(item.Span);
        }

        ReconciliationSymbol symbol = remote.ProduceNext();
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(0, [symbol])), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
    }


    /// <summary>
    /// Two resolutions whose single local-drop dot shares both the replica and the counter are equal, even though
    /// they are built from independent <see cref="ImmutableArray{T}"/> instances.
    /// </summary>
    [TestMethod]
    public void EqualsIsTrueWhenLocalDropsMatchByReplicaAndCounter()
    {
        ImmutableArray<byte> firstReplica = [.. R1.ToArray()];
        ImmutableArray<byte> secondReplica = [.. R1.ToArray()];
        ReconciliationDifferenceResolution<string> first = new([], [], [new DotState(firstReplica, 1)]);
        ReconciliationDifferenceResolution<string> second = new([], [], [new DotState(secondReplica, 1)]);

        Assert.IsTrue(first.Equals(second));
    }


    /// <summary>
    /// The constructor validates every local-drop dot: a replica of the wrong width, a counter below one, and a
    /// dot that repeats an earlier one's replica and counter, are each rejected before the resolution is built.
    /// </summary>
    [TestMethod]
    public void ResolutionConstructorRejectsInvalidLocalDrops()
    {
        ImmutableArray<byte> shortReplica = [.. new byte[ReplicaId.Size - 1]];
        ImmutableArray<DotState> shortReplicaDrops = [new DotState(shortReplica, 1)];
        ArgumentException widthThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], [], shortReplicaDrops));
        Assert.AreEqual("localDrops", widthThrown.ParamName);

        ImmutableArray<byte> replicaA = [.. R1.ToArray()];
        ImmutableArray<DotState> zeroCounterDrops = [new DotState(replicaA, 0)];
        ArgumentException counterThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], [], zeroCounterDrops));
        Assert.AreEqual("localDrops", counterThrown.ParamName);

        ImmutableArray<DotState> duplicateDrops = [new DotState(replicaA, 1), new DotState(replicaA, 1)];
        ArgumentException duplicateThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], [], duplicateDrops));

        Assert.AreEqual("localDrops", duplicateThrown.ParamName);
    }


    /// <summary>
    /// Two resolutions with the same number of push entries but a different entry at the same position are
    /// unequal — the per-position comparison, not merely the count, decides push equality.
    /// </summary>
    [TestMethod]
    public void EqualsIsFalseWhenPushContentDiffersAtTheSameLength()
    {
        ReconciliationElementEntry<string> alpha = new(A1, "alpha");
        ReconciliationElementEntry<string> beta = new(A2, "beta");
        ReconciliationDifferenceResolution<string> first = new([], [alpha]);
        ReconciliationDifferenceResolution<string> second = new([], [beta]);

        Assert.IsFalse(first.Equals(second));
    }


    /// <summary>A remove-aware initiator applying its fetch answer folds the context inline; the terminal merge must not run again.</summary>
    [TestMethod]
    public async Task ARemoveAwareInitiatorsFetchAnswerFoldsTheContextExactlyOnce()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] initiatorItems = [A1];
        ReadOnlyMemory<byte>[] peerItems = [A1, A2];

        int mergeCalls = 0;
        MergeReconciliationContextDelegate recordingMerge = (_, _) =>
        {
            mergeCalls++;

            return ValueTask.CompletedTask;
        };

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, initiatorItems, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) => new ReconciliationDifferenceResolution<string>([.. decoded], []);

        bool fetchSent = false;
        SendReconciliationEnvelopeDelegate<string> send = (envelope, token) =>
        {
            if(envelope.Fetch is not null)
            {
                fetchSent = true;
            }

            return ValueTask.CompletedTask;
        };

        Task initiatorRun = initiator.RunAsync(send, resolve, null, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: recordingMerge, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        using ReconciliationEncoder remote = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in peerItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationEncoder local = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(StructuralContract, BaseMemoryPool.Shared);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        ReconciliationElements<string> answer = new([new ReconciliationElementEntry<string>(A2, "peer-item")]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForElements(answer), cancellationToken).ConfigureAwait(false);

        await initiatorRun.ConfigureAwait(false);

        Assert.IsTrue(fetchSent);
        Assert.AreEqual(0, mergeCalls);
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State);
        Assert.IsTrue(initiator.IsConverged);
    }


    /// <summary>Pins the per-item coverage guard: the count matches, so only a wrong served item can trip this.</summary>
    [TestMethod]
    public async Task AFetchAnswerSubstitutingTheWrongItemFaultsTheResponder()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, BaseMemoryPool.Shared);

        ReadOnlyMemory<byte> wrongItem = SHA256.HashData(Encoding.UTF8.GetBytes("not-requested"));
        ServeReconciliationFetchDelegate<string> serveWrongItem = _ => [new ReconciliationElementEntry<string>(wrongItem, "wrong")];

        //The guard under test lives on a lone responder: an offer then a done signal park it in resolving, and a
        //fetch answer of the right count but the wrong item trips the per-item coverage check with no initiator in the loop.
        Task responderRun = responder.RunAsync(Discard, null, serveWrongItem, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> requestedItem = SHA256.HashData(Encoding.UTF8.GetBytes("zeta"));
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(new ReconciliationFetch([requestedItem])), cancellationToken).ConfigureAwait(false);
        responder.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("A fetch answer must cover exactly the requested items.", fault.Message);
    }


    /// <summary>
    /// The hash code is sensitive to each local-drop dot's replica bytes and counter, and to the aggregate they
    /// feed into the overall hash — not only to the local-drop count.
    /// </summary>
    [TestMethod]
    public void HashCodeIncludesEachLocalDropsReplicaCounterAndAggregate()
    {
        ImmutableArray<byte> replicaA = [.. R1.ToArray()];
        ImmutableArray<byte> replicaB = [.. R2.ToArray()];

        ReconciliationDifferenceResolution<string> sameCounterDifferentReplicaFirst = new([], [], [new DotState(replicaA, 1)]);
        ReconciliationDifferenceResolution<string> sameCounterDifferentReplicaSecond = new([], [], [new DotState(replicaB, 1)]);

        Assert.AreNotEqual(
            sameCounterDifferentReplicaFirst.GetHashCode(),
            sameCounterDifferentReplicaSecond.GetHashCode());

        ReconciliationDifferenceResolution<string> sameReplicaDifferentCounterFirst = new([], [], [new DotState(replicaA, 1)]);
        ReconciliationDifferenceResolution<string> sameReplicaDifferentCounterSecond = new([], [], [new DotState(replicaA, 2)]);

        Assert.AreNotEqual(
            sameReplicaDifferentCounterFirst.GetHashCode(),
            sameReplicaDifferentCounterSecond.GetHashCode());
    }


    /// <summary>A trigger past Reconciling must be a no-op: it must not produce or send a symbol batch.</summary>
    [TestMethod]
    public async Task ATriggerAfterReconcilingIsANoOp()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        int symbolSends = 0;
        SendReconciliationEnvelopeDelegate<string> countingSend = (envelope, token) =>
        {
            if(envelope.Symbols is not null)
            {
                symbolSends++;
            }

            return ValueTask.CompletedTask;
        };

        ServeReconciliationFetchDelegate<string> serve = _ => [];
        Task responderRun = responder.RunAsync(countingSend, null, serve, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await responderRun.ConfigureAwait(false);

        Assert.AreEqual(0, symbolSends);
    }


    /// <summary>Pins the phase guard for symbol batches: the role guard cannot fire since Role is Initiator.</summary>
    [TestMethod]
    public async Task SymbolsBeforeAnOfferFailClosedOnThePhaseGuard()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        ReconciliationSymbolBatch batch = new(0, [new ReconciliationSymbol(A1, new byte[8])]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(batch), cancellationToken).ConfigureAwait(false);
        initiator.Complete();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.Contains("A symbol batch is legal only while reconciling", fault.Message);
    }


    /// <summary>Pins that SubmitAsync rejects a null envelope before enqueueing anything.</summary>
    [TestMethod]
    public async Task SubmitAsyncRejectsANullEnvelope()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> session = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => session.SubmitAsync(null!, TestContext.CancellationToken).AsTask()).ConfigureAwait(false);
    }


    /// <summary>Pins the phase guard for a causal context specifically, not the duplicate-context guard it would otherwise be masked by.</summary>
    [TestMethod]
    public async Task ACausalContextAfterTheDoneSignalFailsClosedOnThePhaseGuard()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);

        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, ApplyNoElements, applyDrops: ApplyNoDrops, mergeContext: NoMerge, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), cancellationToken).ConfigureAwait(false);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForContext(new ReconciliationContext(SamplePeerContext)), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.Contains("legal only while pinning or reconciling", fault.Message);
    }


    /// <summary>Pins the eager remove-aware validation that a terminal context merger is required, isolated from the sibling elements-applier guard by asserting ParamName.</summary>
    [TestMethod]
    public async Task RunAsyncRejectsARemoveAwareSessionMissingTheContextMerger()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        //The context-merger guard is identified by its parameter name because the sibling applier guards throw the same type.
        ArgumentNullException fault = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => responder.RunAsync(Discard, null, serve, null, applyDrops: ApplyNoDrops, mergeContext: null, cancellationToken: TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("mergeContext", fault.ParamName);
    }


    /// <summary>
    /// The constructor validates every push entry: a null first entry, a null later entry, a later entry whose
    /// item width disagrees with the first entry's, and a later entry whose item repeats an earlier one, are each
    /// rejected before the resolution is built.
    /// </summary>
    [TestMethod]
    public void ResolutionConstructorRejectsInvalidPushEntries()
    {
        ImmutableArray<ReconciliationElementEntry<string>> nullFirstPush = [null!];
        ArgumentException nullFirstThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], nullFirstPush));
        Assert.AreEqual("push", nullFirstThrown.ParamName);

        ReconciliationElementEntry<string> validFirst = new(A1, "alpha");
        ImmutableArray<ReconciliationElementEntry<string>> nullSecondPush = [validFirst, null!];
        ArgumentException nullSecondThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], nullSecondPush));
        Assert.AreEqual("push", nullSecondThrown.ParamName);

        byte[] narrowItem = [0x01, 0x02, 0x03, 0x04];
        ReconciliationElementEntry<string> narrowEntry = new(narrowItem, "narrow");
        ImmutableArray<ReconciliationElementEntry<string>> mismatchedWidthPush = [validFirst, narrowEntry];
        ArgumentException widthThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], mismatchedWidthPush));
        Assert.AreEqual("push", widthThrown.ParamName);

        ReconciliationElementEntry<string> duplicateItem = new(A1, "beta");
        ImmutableArray<ReconciliationElementEntry<string>> duplicatePush = [validFirst, duplicateItem];
        ArgumentException duplicateThrown = Assert.ThrowsExactly<ArgumentException>(
            () => new ReconciliationDifferenceResolution<string>([], duplicatePush));

        Assert.AreEqual("push", duplicateThrown.ParamName);
    }


    /// <summary>An elements message before resolving is refused by the phase guard, identified by its message.</summary>
    [TestMethod]
    public async Task ElementsBeforeResolvingFailClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        ReconciliationElements<string> elements = new([new ReconciliationElementEntry<string>(A1, "x")]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForElements(elements), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("An elements message is legal only while resolving.", fault.Message);
    }


    /// <summary>
    /// The push comparison loop advances forward through matching entries to completion instead of walking
    /// backward off the start of the array.
    /// </summary>
    [TestMethod]
    public void EqualsCompletesForwardIterationWhenPushEntriesMatch()
    {
        ReconciliationElementEntry<string> firstAlpha = new(A1, "alpha");
        ReconciliationElementEntry<string> secondAlpha = new(A1, "alpha");
        ReconciliationDifferenceResolution<string> first = new([], [firstAlpha]);
        ReconciliationDifferenceResolution<string> second = new([], [secondAlpha]);

        Assert.IsTrue(first.Equals(second));
    }


    /// <summary>An add-only responder refuses a received drop at the add-only guard, the first check in the drop handler, identified by its message.</summary>
    [TestMethod]
    public async Task AnAddOnlyResponderRejectsAReceivedDrop()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task responderRun = responder.RunAsync(Discard, null, serve, null, cancellationToken: cancellationToken);

        ImmutableArray<byte> replica = [.. new byte[ReplicaId.Size]];
        ReconciliationDrop drop = new([new DotState(replica, 1)]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDrop(drop), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("An add-only session must not receive a drop.", fault.Message);
    }


    /// <summary>Pins that a responder, which never decodes, reports an empty list rather than null.</summary>
    [TestMethod]
    public void DecodedItemsIsEmptyNotNullForAResponder()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);

        Assert.IsEmpty(responder.DecodedItems);
    }


    /// <summary>A fetch on an initiator is refused by the role guard, identified by its message.</summary>
    [TestMethod]
    public async Task AFetchOnAnInitiatorFailsClosed()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        ReconciliationFetch fetch = new([A1]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(fetch), cancellationToken).ConfigureAwait(false);

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("Only a responder serves a fetch.", fault.Message);
    }


    /// <summary>
    /// Two resolutions with equal fetch counts but a different push-entry count, or a different local-drop count,
    /// are unequal — each count is checked on its own, not folded into the other counts.
    /// </summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenPushOrLocalDropsCountsDiffer()
    {
        ReconciliationElementEntry<string> entry = new(A1, "alpha");
        ReconciliationDifferenceResolution<string> withPush = new([], [entry]);
        ReconciliationDifferenceResolution<string> withoutPush = new([], []);

        Assert.IsFalse(withPush.Equals(withoutPush));

        ImmutableArray<byte> replica = [.. R1.ToArray()];
        ReconciliationDifferenceResolution<string> withoutDrops = new([], [], []);
        ReconciliationDifferenceResolution<string> withDrops = new([], [], [new DotState(replica, 1)]);

        Assert.IsFalse(withoutDrops.Equals(withDrops));
    }


    /// <summary>
    /// Drives a two-session exchange to the initiator's completion without polling: a local mirror decoder over the
    /// same items and coded symbols names the exact symbol count the initiator's decoder needs, so the responder is
    /// triggered just enough batches to deliver them, and the ordered channel then carries the done, fetch, and
    /// answer round-trip through to <see cref="AntiEntropySessionState.Completed"/> with no further trigger.
    /// </summary>
    private static async Task DriveInitiatorToCompletionAsync(
        Task initiatorRun,
        AntiEntropySession<string> responder,
        ReconciliationContract contract,
        IReadOnlyList<ReadOnlyMemory<byte>> initiatorItems,
        IReadOnlyList<ReadOnlyMemory<byte>> responderItems,
        CancellationToken cancellationToken)
    {
        using ReconciliationEncoder local = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(contract, BaseMemoryPool.Shared);
        int symbols = 0;
        while(!mirror.IsComplete)
        {
            mirror.Absorb(local.ProduceNext().Combine(remote.ProduceNext()));
            symbols++;
        }

        int batches = (symbols + responder.BatchSize - 1) / responder.BatchSize;
        for(int batch = 0; batch < batches; batch++)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
        }

        await initiatorRun.ConfigureAwait(false);
    }


    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    /// <summary>
    /// A run started on an empty inbound channel never posts to the caller's synchronization context, whether
    /// the yield has already moved it to the pool when work arrives or the reads complete synchronously, so a
    /// host that blocks its starting thread on the run cannot deadlock it.
    /// </summary>
    [TestMethod]
    public async Task ARunStartedOnAnEmptyChannelDoesNotPostToTheCallersContext()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        PostCountingSynchronizationContext context = new();

        Task run = context.Start(() => initiator.RunAsync(Discard, resolve, null, null, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(run.IsCompleted);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), TestContext.CancellationToken).ConfigureAwait(false);
        initiator.Complete();
        await run.ConfigureAwait(false);

        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// The offer send is the run's first act and may park on a host whose transport is not ready; its resumption
    /// stays off the caller's synchronization context, so a host that blocks its starting thread on the run
    /// cannot deadlock it while the offer is in flight.
    /// </summary>
    [TestMethod]
    public async Task AnOfferSendParkedByTheHostDoesNotResumeOnTheCallersContext()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BaseMemoryPool.Shared);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        TaskCompletionSource offerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SendReconciliationEnvelopeDelegate<string> gatedSend = (_, _) => new ValueTask(offerGate.Task);
        PostCountingSynchronizationContext context = new();

        Task run = context.Start(() => initiator.RunAsync(gatedSend, resolve, null, null, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(run.IsCompleted);

        offerGate.SetResult();
        initiator.Complete();
        await run.ConfigureAwait(false);

        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// A remove-aware session's context send follows a synchronously completed offer send, so it is reached with
    /// the caller's synchronization context still current; its resumption stays off that context, so a host that
    /// blocks its starting thread on the run cannot deadlock it while the context is in flight.
    /// </summary>
    [TestMethod]
    public async Task AContextSendParkedByTheHostDoesNotResumeOnTheCallersContext()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items, BatchSize, BaseMemoryPool.Shared, localContext: EmptyContext);
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        TaskCompletionSource contextGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SendReconciliationEnvelopeDelegate<string> gatedSend = (envelope, _) =>
            envelope.Context is not null ? new ValueTask(contextGate.Task) : ValueTask.CompletedTask;
        PostCountingSynchronizationContext context = new();

        Task run = context.Start(() => initiator.RunAsync(gatedSend, resolve, null, null, ApplyNoDrops, NoMerge, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(run.IsCompleted);

        contextGate.SetResult();
        initiator.Complete();
        await run.ConfigureAwait(false);

        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// A host may queue envelopes before starting the run, which makes the first channel read complete
    /// synchronously; the run still leaves the caller's thread before dispatching, so the dispatch work runs
    /// off the starting thread and a dispatch that parks on the host's send resumes off the caller's
    /// synchronization context — a host that blocks its starting thread on the run cannot deadlock it.
    /// </summary>
    [TestMethod]
    public async Task APreQueuedDispatchParkedOnTheHostsSendDoesNotResumeOnTheCallersContext()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items, BaseMemoryPool.Shared);
        bool served = false;
        SynchronizationContext? servedUnder = null;
        ServeReconciliationFetchDelegate<string> serve = requested =>
        {
            served = true;
            servedUnder = SynchronizationContext.Current;

            return [.. requested.Select(item => new ReconciliationElementEntry<string>(item, "alpha"))];
        };

        TaskCompletionSource elementsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SendReconciliationEnvelopeDelegate<string> gatedSend = (envelope, _) =>
            envelope.Elements is not null ? new ValueTask(elementsGate.Task) : ValueTask.CompletedTask;
        PostCountingSynchronizationContext context = new();

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), TestContext.CancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(1)), TestContext.CancellationToken).ConfigureAwait(false);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForFetch(new ReconciliationFetch([A1])), TestContext.CancellationToken).ConfigureAwait(false);

        Task run = context.Start(() => responder.RunAsync(gatedSend, null, serve, null, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(run.IsCompleted);

        elementsGate.SetResult();
        responder.Complete();
        await run.ConfigureAwait(false);

        Assert.AreEqual(0, context.Posts);
        Assert.IsTrue(served);
        Assert.IsNull(servedUnder);
    }


    private static ValueTask ForwardTo(AntiEntropySession<string> peer, ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
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


    private static ValueTask Discard(ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }


    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch(InvalidOperationException)
        {
            //The peer of a faulting run may itself fault or be torn down; the asserted side owns the outcome.
        }
        catch(OperationCanceledException)
        {
            //A cancelled run is an expected teardown path under the linked timeout.
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


    private static Dictionary<string, string> BuildHashDirectory(OrSet<string> set)
    {
        Dictionary<string, string> directory = [];
        foreach(string element in set.Elements)
        {
            directory[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))] = element;
        }

        return directory;
    }


    private static string[] Sorted(OrSet<string> set)
    {
        return [.. set.Elements.Order()];
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
