using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>Proves session convergence, lazy enumeration, and fail-closed snapshot passes across both roles and encoder forms.</summary>
[TestClass]
internal sealed class AntiEntropySessionSnapshotTests
{
    /// <summary>The hang guard bounding a stalled exchange.</summary>
    private static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The fetch server for an exchange with no requested items.</summary>
    private static ServeReconciliationFetchDelegate<string> ServeNothing { get; } = _ => [];

    /// <summary>The contract for fixed-width structural guard vectors.</summary>
    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    /// <summary>The contract for content-hash convergence vectors.</summary>
    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    /// <summary>A structural item of the contract's width.</summary>
    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    /// <summary>The ancestor's replica identity.</summary>
    private static ReplicaId R1 { get; } = Replica(1);

    /// <summary>The initiator's replica identity.</summary>
    private static ReplicaId R2 { get; } = Replica(2);

    /// <summary>The responder's replica identity.</summary>
    private static ReplicaId R3 { get; } = Replica(3);

    /// <summary>The elements both replicas hold after convergence.</summary>
    private static string[] ExpectedConverged { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    /// <summary>The ordinal-sorted digests of the divergent elements.</summary>
    private static string[] ExpectedDifference { get; } = [.. new[] { "delta", "epsilon", "zeta" }
        .Select(element => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))).Order(StringComparer.Ordinal)];

    /// <summary>The digest the initiator fetches from the divergent responder.</summary>
    private static string[] ExpectedFetch { get; } = [Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zeta")))];

    /// <summary>The ordinal-sorted digests the divergent initiator pushes.</summary>
    private static string[] ExpectedPush { get; } = [.. new[] { "delta", "epsilon" }
        .Select(element => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))).Order(StringComparer.Ordinal)];

    /// <summary>The test context supplying cancellation and diagnostics.</summary>
    public TestContext TestContext { get; set; } = null!;


    /// <summary>An incremental pair converges and then reaches quiescence at every tested batch size.</summary>
    [TestMethod]
    public async Task AnIncrementalPairConvergesThenReachesQuiescence()
    {
        await RunFormProofAsync(initiatorSnapshot: false, responderSnapshot: false).ConfigureAwait(false);
    }


    /// <summary>An incremental initiator and snapshot responder converge and then reach quiescence at every tested batch size.</summary>
    [TestMethod]
    public async Task AnIncrementalInitiatorAndSnapshotResponderConvergeThenReachQuiescence()
    {
        await RunFormProofAsync(initiatorSnapshot: false, responderSnapshot: true).ConfigureAwait(false);
    }


    /// <summary>A snapshot initiator and incremental responder converge and then reach quiescence at every tested batch size.</summary>
    [TestMethod]
    public async Task ASnapshotInitiatorAndIncrementalResponderConvergeThenReachQuiescence()
    {
        await RunFormProofAsync(initiatorSnapshot: true, responderSnapshot: false).ConfigureAwait(false);
    }


    /// <summary>A snapshot pair converges and then reaches quiescence at every tested batch size.</summary>
    [TestMethod]
    public async Task ASnapshotPairConvergesThenReachesQuiescence()
    {
        await RunFormProofAsync(initiatorSnapshot: true, responderSnapshot: true).ConfigureAwait(false);
    }


    /// <summary>Runs divergent and quiescent rounds over fresh sessions for each batch size and encoder combination.</summary>
    private async Task RunFormProofAsync(bool initiatorSnapshot, bool responderSnapshot)
    {
        foreach(int batchSize in new[] { 1, 4, 256 })
        {
            OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
            OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
            OrSet<string> responderSet = ancestor.Add("zeta", R3);

            (OrSet<string> initiatorResult, OrSet<string> responderResult) = await RunRoundAsync(
                initiatorSet, responderSet, initiatorSnapshot, responderSnapshot, batchSize, quiescent: false).ConfigureAwait(false);
            await RunRoundAsync(initiatorResult, responderResult, initiatorSnapshot, responderSnapshot, batchSize, quiescent: true).ConfigureAwait(false);
        }
    }


    /// <summary>Runs one pair to completion and verifies its decoded difference, transfers, final states, and pass counts.</summary>
    private async Task<(OrSet<string> InitiatorSet, OrSet<string> ResponderSet)> RunRoundAsync(
        OrSet<string> initiatorSet, OrSet<string> responderSet, bool initiatorSnapshot, bool responderSnapshot, int batchSize, bool quiescent)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        string label = $"Initiator {(initiatorSnapshot ? "snapshot" : "incremental")}, responder {(responderSnapshot ? "snapshot" : "incremental")}, batch size {batchSize}, quiescent {quiescent}";
        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        CountingEnumeration initiatorEnumeration = new(initiatorItems);
        CountingEnumeration responderEnumeration = new(responderItems);
        using AntiEntropySession<string> initiator = initiatorSnapshot
            ? new(AntiEntropyRole.Initiator, ContentHashContract, initiatorEnumeration.Enumerate, batchSize, BaseMemoryPool.Shared)
            : new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, batchSize, BaseMemoryPool.Shared);
        using AntiEntropySession<string> responder = responderSnapshot
            ? new(AntiEntropyRole.Responder, ContentHashContract, responderEnumeration.Enumerate, batchSize, BaseMemoryPool.Shared)
            : new(AntiEntropyRole.Responder, ContentHashContract, responderItems, batchSize, BaseMemoryPool.Shared);
        Assert.AreEqual(0, initiatorEnumeration.Calls, $"{label}: initiator construction must not enumerate.");
        Assert.AreEqual(0, responderEnumeration.Calls, $"{label}: responder construction must not enumerate.");
        string[] fetched = [];
        string[] pushed = [];

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

            ReconciliationDifferenceResolution<string> resolution = new(fetch.ToImmutable(), push.ToImmutable());
            fetched = [.. resolution.Fetch.Select(item => Convert.ToHexString(item.Span)).Order(StringComparer.Ordinal)];
            pushed = [.. resolution.Push.Select(entry => Convert.ToHexString(entry.Item.Span)).Order(StringComparer.Ordinal)];

            return resolution;
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

        int batches = await DriveInitiatorToCompletionAsync(initiatorRun, responder, ContentHashContract, initiatorItems, responderItems, cancellationToken).ConfigureAwait(false);

        responder.Complete();
        await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

        string[] expectedDifference = quiescent ? [] : ExpectedDifference;
        string[] expectedFetch = quiescent ? [] : ExpectedFetch;
        string[] expectedPush = quiescent ? [] : ExpectedPush;
        string[] decoded = [.. initiator.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order(StringComparer.Ordinal)];
        Assert.AreSequenceEqual(expectedDifference, decoded, $"{label}: decoded difference.");
        Assert.AreSequenceEqual(expectedFetch, fetched, $"{label}: fetched digests.");
        Assert.AreSequenceEqual(expectedPush, pushed, $"{label}: pushed digests.");
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(initiatorResult), $"{label}: initiator elements.");
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(responderResult), $"{label}: responder elements.");
        Assert.AreEqual(AntiEntropySessionState.Completed, initiator.State, $"{label}: initiator state.");
        Assert.AreEqual(AntiEntropySessionState.Completed, responder.State, $"{label}: responder state.");
        Assert.IsTrue(initiator.IsConverged, $"{label}: initiator convergence.");
        Assert.IsTrue(responder.IsConverged, $"{label}: responder convergence.");
        if(initiatorSnapshot)
        {
            Assert.AreEqual(batches, initiatorEnumeration.Calls, $"{label}: one initiator pass per batch.");
        }

        if(responderSnapshot)
        {
            Assert.AreEqual(batches, responderEnumeration.Calls, $"{label}: one responder pass per trigger.");
        }

        if(quiescent)
        {
            Assert.AreEqual(1, batches, $"{label}: quiescence takes one trigger.");
            Assert.AreSequenceEqual(Sorted(initiatorSet), Sorted(initiatorResult), $"{label}: initiator remains unchanged.");
            Assert.AreSequenceEqual(Sorted(responderSet), Sorted(responderResult), $"{label}: responder remains unchanged.");
        }

        return (initiatorResult, responderResult);
    }


    /// <summary>A straggler after resolving leaves the enumeration count and decoded difference unchanged.</summary>
    [TestMethod]
    public async Task AStragglerBatchAfterResolvingDoesNotEnumerate()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1);
        OrSet<string> initiatorSet = ancestor;
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
        ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

        CountingEnumeration counting = new(initiatorItems);
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, counting.Enumerate, 4, BaseMemoryPool.Shared);

        //The initiator lacks zeta, so after decoding it fetches and parks in Resolving with an outstanding answer.
        ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            new ReconciliationDifferenceResolution<string>([.. decoded], []);
        ApplyReconciliationElementsDelegate<string> applyNothing = (_, _, ct) => new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);

        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, applyNothing, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(ContentHashContract)), cancellationToken).ConfigureAwait(false);

        //A local mirror reproduces the initiator's decoder over the same items and the peer's coded symbols, so it
        //names the exact prefix that completes the decode and parks the initiator in Resolving with its fetch
        //outstanding; the mirror's decoded count is the count a later straggler must leave untouched.
        using ReconciliationEncoder local = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(ContentHashContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(ContentHashContract, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        while(!mirror.IsComplete)
        {
            int startIndex = remote.ProducedCount;
            ReconciliationSymbol symbol = remote.ProduceNext();
            mirror.Absorb(local.ProduceNext().Combine(symbol));
            await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(startIndex, [symbol])), cancellationToken).ConfigureAwait(false);
        }

        int absorbedAtResolving = mirror.DecodedItems.Count;
        int batches = remote.ProducedCount;

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
        Assert.AreEqual(batches, counting.Calls);
    }


    /// <summary>A throwing snapshot enumeration propagates its original exception out of the responder run.</summary>
    [TestMethod]
    public async Task AThrowingEnumerationFaultsTheResponderRun()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;
        int calls = 0;
        EnumerateReconciliationItemsDelegate enumerate = (_, _) =>
        {
            calls++;
            throw new IOException("The snapshot store is unavailable.");
        };
        using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, StructuralContract, enumerate, 4, BaseMemoryPool.Shared);
        Task responderRun = responder.RunAsync(Discard, null, ServeNothing, null, cancellationToken: cancellationToken);

        await responder.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);

        IOException fault = await Assert.ThrowsExactlyAsync<IOException>(() => responderRun).ConfigureAwait(false);
        Assert.AreEqual("The snapshot store is unavailable.", fault.Message);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(responder.IsConverged);
    }


    /// <summary>A throwing snapshot enumeration faults the initiator run before the resolver can be invoked.</summary>
    [TestMethod]
    public async Task AThrowingEnumerationFaultsTheInitiatorRun()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;
        int calls = 0;
        EnumerateReconciliationItemsDelegate enumerate = (_, _) =>
        {
            calls++;
            throw new IOException("The snapshot store is unavailable.");
        };
        int resolverCalls = 0;
        ResolveReconciliationDifferenceDelegate<string> resolve = (_, _) =>
        {
            resolverCalls++;

            return ReconciliationDifferenceResolution<string>.Empty;
        };
        using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, StructuralContract, enumerate, 4, BaseMemoryPool.Shared);
        Task initiatorRun = initiator.RunAsync(Discard, resolve, null, null, cancellationToken: cancellationToken);

        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(StructuralContract)), cancellationToken).ConfigureAwait(false);
        await initiator.SubmitAsync(ReconciliationEnvelope<string>.ForSymbols(new ReconciliationSymbolBatch(0, [new ReconciliationSymbol(A1, new byte[8])])), cancellationToken).ConfigureAwait(false);

        IOException fault = await Assert.ThrowsExactlyAsync<IOException>(() => initiatorRun).ConfigureAwait(false);
        Assert.AreEqual("The snapshot store is unavailable.", fault.Message);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(initiator.IsConverged);
        Assert.AreEqual(0, resolverCalls);
    }


    /// <summary>The enumeration constructor rejects each invalid argument in the session's validation order.</summary>
    [TestMethod]
    public void ConstructorValidationRejectsBadArgumentsForTheEnumerationForm()
    {
        CountingEnumeration counting = new([A1]);
        ArgumentOutOfRangeException roleFault = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AntiEntropySession<string>((AntiEntropyRole)0, StructuralContract, counting.Enumerate, batchSize: 0, BaseMemoryPool.Shared));
        Assert.AreEqual("role", roleFault.ParamName);

        ArgumentNullException contractFault = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, null!, counting.Enumerate, batchSize: 0, BaseMemoryPool.Shared));
        Assert.AreEqual("contract", contractFault.ParamName);

        ArgumentNullException enumerateFault = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, enumerate: null!, batchSize: 0, BaseMemoryPool.Shared));
        Assert.AreEqual("enumerate", enumerateFault.ParamName);

        ArgumentNullException poolFault = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, counting.Enumerate, batchSize: 0, pool: null!));
        Assert.AreEqual("pool", poolFault.ParamName);

        ArgumentOutOfRangeException batchSizeFault = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, counting.Enumerate, batchSize: 0, BaseMemoryPool.Shared));
        Assert.AreEqual("batchSize", batchSizeFault.ParamName);
    }


    /// <summary>A negative counter in the local context is rejected at construction without enumerating the snapshot.</summary>
    [TestMethod]
    public void AnEnumerationSessionRejectsANegativeCounterLocalContextAtConstruction()
    {
        CountingEnumeration counting = new([A1]);
        VectorClockState malformed = new([new ReplicaCounterEntry(ImmutableArray.Create(new byte[ReplicaId.Size]), -1)]);

        ArgumentException fault = Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using AntiEntropySession<string> session = new(AntiEntropyRole.Responder, StructuralContract, counting.Enumerate, 4, BaseMemoryPool.Shared, malformed);
        });

        Assert.AreEqual("state", fault.ParamName);
        Assert.AreEqual(0, counting.Calls);
    }


    /// <summary>Construction and disposal leave both roles' snapshot sources unenumerated.</summary>
    [TestMethod]
    public void ConstructingAndDisposingASnapshotSessionNeverEnumerates()
    {
        foreach(AntiEntropyRole role in new[] { AntiEntropyRole.Initiator, AntiEntropyRole.Responder })
        {
            CountingEnumeration counting = new([A1]);
            using AntiEntropySession<string> session = new(role, StructuralContract, counting.Enumerate, 4, BaseMemoryPool.Shared);
            Assert.AreEqual(0, counting.Calls, $"{role}: construction must not enumerate.");
            session.Dispose();
            Assert.AreEqual(0, counting.Calls, $"{role}: disposal must not enumerate.");
        }
    }


    /// <summary>Drives the exact mirrored prefix to completion and returns the number of batch triggers sent.</summary>
    private static async Task<int> DriveInitiatorToCompletionAsync(
        Task initiatorRun,
        AntiEntropySession<string> responder,
        ReconciliationContract contract,
        IReadOnlyList<ReadOnlyMemory<byte>> initiatorItems,
        IReadOnlyList<ReadOnlyMemory<byte>> responderItems,
        CancellationToken cancellationToken)
    {
        using ReconciliationEncoder local = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(contract, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
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

        return batches;
    }


    /// <summary>Creates a sender that forwards envelopes to the peer session.</summary>
    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    /// <summary>Forwards an envelope to a peer and drops a late send to a closed channel.</summary>
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


    /// <summary>Discards an envelope and completes synchronously.</summary>
    private static ValueTask Discard(ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }


    /// <summary>Projects each set element to its content digest.</summary>
    private static ReadOnlyMemory<byte>[] ProjectHashes(OrSet<string> set)
    {
        List<ReadOnlyMemory<byte>> items = [];
        foreach(string element in set.Elements)
        {
            items.Add(SHA256.HashData(Encoding.UTF8.GetBytes(element)));
        }

        return [.. items];
    }


    /// <summary>Maps each content digest to the set element it identifies.</summary>
    private static Dictionary<string, string> BuildHashDirectory(OrSet<string> set)
    {
        Dictionary<string, string> directory = [];
        foreach(string element in set.Elements)
        {
            directory[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))] = element;
        }

        return directory;
    }


    /// <summary>Returns the set elements in sorted order.</summary>
    private static string[] Sorted(OrSet<string> set)
    {
        return [.. set.Elements.Order()];
    }


    /// <summary>Creates a replica identity from a fixed leading byte.</summary>
    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>Counts the enumeration calls over one stable array of snapshot items.</summary>
    private sealed class CountingEnumeration
    {
        /// <summary>Initializes a source over the supplied pinned snapshot items.</summary>
        public CountingEnumeration(ReadOnlyMemory<byte>[] items)
        {
            Items = items;
        }

        /// <summary>The pinned snapshot items in their stable enumeration order.</summary>
        private ReadOnlyMemory<byte>[] Items { get; }

        /// <summary>The number of enumeration calls.</summary>
        public int Calls { get; private set; }

        /// <summary>Counts a pass and visits each item once while observing cancellation before every visit.</summary>
        public void Enumerate(VisitReconciliationItemDelegate visit, CancellationToken cancellationToken)
        {
            Calls++;
            foreach(ReadOnlyMemory<byte> item in Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visit(item.Span);
            }
        }
    }
}
