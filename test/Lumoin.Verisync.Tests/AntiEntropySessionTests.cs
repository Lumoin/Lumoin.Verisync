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
/// tolerance, the submit shape guard, snapshot pinning, and the resolution record's validation and equality.
/// </summary>
[TestClass]
internal sealed class AntiEntropySessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 100;

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

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems);

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

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedConverged, Sorted(initiatorResult));
        CollectionAssert.AreEqual(ExpectedConverged, Sorted(responderResult));
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
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items);

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

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.HasCount(0, initiator.DecodedItems);
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
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, otherContract, items);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);

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
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;
        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        //A trigger addressed to an initiator is caller error and throws synchronously, before any run begins.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiator.TriggerBatchAsync(cancellationToken).AsTask()).ConfigureAwait(false);

        ReconciliationSymbolBatch batch = new(0, [new ReconciliationSymbol(A1, new byte[8])]);
        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(batch), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => responderRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AGapInTheSymbolStreamFaultsTheInitiator()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, items);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items);

        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) => ReconciliationDifferenceResolution<string>.Empty;

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, null, cancellationToken: cancellationToken);

        //The decoder stands at AbsorbedCount zero, so a batch starting one ahead at index one is a gap.
        ReconciliationSymbolBatch forged = new(1, [new ReconciliationSymbol(A1, new byte[8])]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(forged), cancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => initiatorRun).ConfigureAwait(false);

        responder.Complete();
    }


    [TestMethod]
    public void ConstructorValidationRejectsBadArguments()
    {
        ReadOnlyMemory<byte>[] items = [A1, A2];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AntiEntropySession<string>((AntiEntropyRole)0, StructuralContract, items));
        Assert.ThrowsExactly<ArgumentNullException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, null!, items));
        Assert.ThrowsExactly<ArgumentNullException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, items, 0));

        ReadOnlyMemory<byte>[] wrongWidth = [new byte[4]];
        Assert.ThrowsExactly<ArgumentException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, wrongWidth));

        ReadOnlyMemory<byte>[] duplicates = [A1, A1.ToArray()];
        Assert.ThrowsExactly<ArgumentException>(() => new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, duplicates));
    }


    [TestMethod]
    public async Task RunValidationRejectsMissingRoleDelegatesAndASecondRun()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];

        using AntiEntropySession<string> missingResolve = new(AntiEntropyRole.Initiator, StructuralContract, items);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => missingResolve.RunAsync(Discard, null, null, null, cancellationToken: cancellationToken)).ConfigureAwait(false);

        using AntiEntropySession<string> missingServe = new(AntiEntropyRole.Responder, StructuralContract, items);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => missingServe.RunAsync(Discard, null, null, null, cancellationToken: cancellationToken)).ConfigureAwait(false);

        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, items);
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
        OrSet<string> initiatorSet = ancestor;
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems);

        //The initiator lacks zeta, so it fetches it; the responder answers with no entries, which fails the
        //coverage check on the responder's run.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            new ReconciliationDifferenceResolution<string>([.. decoded], []);

        ServeReconciliationFetchDelegate<string> serveTooFew = _ => [];
        ApplyReconciliationElementsDelegate<string> applyNothing = (_, _, ct) => new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyNothing, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serveTooFew, null, cancellationToken: cancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => PaceFaultAsync(responderRun, initiator, responder, cancellationToken)).ConfigureAwait(false);

        //The initiator parked in Resolving awaiting an answer that never comes; completing it drains its loop.
        initiator.Complete();
        responder.Complete();
        await SwallowAsync(initiatorRun).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ElementsWithoutAnApplyHookFaultsTheReceivingSide()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2);
        OrSet<string> responderSet = ancestor;

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems);

        Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);

        //The initiator holds delta in surplus and pushes it; the responder has no apply hook, so its run faults
        //on the inbound elements frame.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
        {
            ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
            foreach(ReadOnlyMemory<byte> item in decoded)
            {
                push.Add(new ReconciliationElementEntry<string>(item, initiatorDirectory[Convert.ToHexString(item.Span)]));
            }

            return new ReconciliationDifferenceResolution<string>([], push.ToImmutable());
        };

        ServeReconciliationFetchDelegate<string> serve = _ => [];

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, null, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => PaceFaultAsync(responderRun, initiator, responder, cancellationToken)).ConfigureAwait(false);

        responder.Complete();
        await SwallowAsync(initiatorRun).ConfigureAwait(false);
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

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems);

        Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);

        //The initiator lacks zeta, so after decoding it fetches and parks in Resolving with an outstanding answer.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            new ReconciliationDifferenceResolution<string>([.. decoded], []);

        ServeReconciliationFetchDelegate<string> serve = items =>
            [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];
        ApplyReconciliationElementsDelegate<string> applyNothing = (_, _, ct) => new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);

        Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyNothing, cancellationToken: cancellationToken);
        Task responderRun = responder.RunAsync(Forward(initiator), null, serve, null, cancellationToken: cancellationToken);

        //Pace until the initiator leaves Reconciling for Resolving, then submit a well-formed straggler batch
        //continuing the stream: it must be ignored, leaving the absorbed count untouched, and the run completes.
        int triggers = 0;
        while(initiator.State == AntiEntropySessionState.Created || initiator.State == AntiEntropySessionState.Pinning || initiator.State == AntiEntropySessionState.Reconciling)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The initiator never left Reconciling within the trigger cap.");
        }

        int absorbedAtResolving = initiator.DecodedItems.Count;
        using ReconciliationEncoder straggler = new(ContentHashContract);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            straggler.Add(item.Span);
        }

        int produced = straggler.ProducedCount;
        ReconciliationSymbol next = straggler.ProduceNext();
        ReconciliationSymbolBatch continuation = new(produced, [next]);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(continuation), cancellationToken).ConfigureAwait(false);

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        Assert.HasCount(absorbedAtResolving, initiator.DecodedItems);
    }


    [TestMethod]
    public async Task SubmitAsyncRejectsEnvelopesWithoutExactlyOnePayload()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        ReadOnlyMemory<byte>[] items = [A1, A2];
        using AntiEntropySession<string> session = new(AntiEntropyRole.Initiator, StructuralContract, items);

        ReconciliationEnvelope<string> empty = new(null, null, null, null, null, null, null);
        ReconciliationOffer offer = ReconciliationOffer.FromContract(StructuralContract);
        ReconciliationDone done = new(1);
        ReconciliationEnvelope<string> two = new(offer, null, done, null, null, null, null);

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

        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorBuffers);
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderBuffers);

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

        await PaceUntilInitiatorCompletesAsync(initiator, responder, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedConverged, Sorted(initiatorResult));
        CollectionAssert.AreEqual(ExpectedConverged, Sorted(responderResult));
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

        Assert.HasCount(0, ReconciliationDifferenceResolution<string>.Empty.Fetch);
        Assert.HasCount(0, ReconciliationDifferenceResolution<string>.Empty.Push);

        //Equal contents from independent buffers compare equal with equal hash codes.
        ReadOnlyMemory<byte> left = A1;
        ReadOnlyMemory<byte> right = A1.ToArray();
        ReconciliationDifferenceResolution<string> first = new([left], []);
        ReconciliationDifferenceResolution<string> second = new([right], []);
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }


    private static async Task PaceUntilInitiatorCompletesAsync(AntiEntropySession<string> initiator, AntiEntropySession<string> responder, CancellationToken cancellationToken)
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


    //Paces the responder while awaiting a run task expected to fault; the trigger loop keeps the symbol stream
    //flowing so the choreography reaches the faulting message, and stops the moment that run completes.
    private static async Task PaceFaultAsync(Task faultingRun, AntiEntropySession<string> initiator, AntiEntropySession<string> responder, CancellationToken cancellationToken)
    {
        int triggers = 0;
        while(!faultingRun.IsCompleted && initiator.State != AntiEntropySessionState.Completed)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The faulting run never surfaced within the trigger cap.");
        }

        await faultingRun.ConfigureAwait(false);
    }


    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
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
