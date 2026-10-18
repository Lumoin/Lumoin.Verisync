using Lumoin.Base;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The phase 2 socket proof re-expressed through the session runner: two rounds over fresh loopback
/// connections and fresh sessions, proving the runner composes with the real transport. Each side owns a
/// reader loop forwarding every framed envelope to its session's <see cref="AntiEntropySession{TElement}.SubmitAsync"/>
/// and a send delegate writing the side's <see cref="MessageChannelWriter{TMessage}"/>; only the consumer loop
/// sends, so a single shared writer is safe. The host paces the responder until its state leaves
/// <see cref="AntiEntropySessionState.Reconciling"/>. Round one converges the add-only divergence; round two
/// over the converged sets reaches quiescence.
/// </summary>
[TestClass]
internal sealed class AntiEntropySocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 100;

    private static ReconciliationContract Contract { get; } = ReconciliationContract.ContentHashDefault;

    private static SerializeMessageDelegate<ReconciliationEnvelope<string>> Serialize { get; } =
        ReconciliationJson.CreateEnvelopeSerializer<string>((writer, value) => writer.WriteStringValue(value));

    private static DeserializeMessageDelegate<ReconciliationEnvelope<string>> Deserialize { get; } =
        ReconciliationJson.CreateEnvelopeDeserializer<string>(Contract, element => element.GetString()!);

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);

    private static string[] ExpectedConverged { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task TwoRoundSessionReconciliationConvergesThenReachesQuiescenceOverASocket()
    {
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        //Round one: the add-only divergence converges through one full session over a fresh socket.
        RoundOutcome first = await RunRoundAsync(initiatorSet, responderSet).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedConverged, Sorted(first.InitiatorSet));
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(first.ResponderSet));
        Assert.AreEqual(3, first.DecodedCount);

        //Round two: a fresh session over the converged sets reaches quiescence with zero decoded after one batch.
        RoundOutcome second = await RunRoundAsync(first.InitiatorSet, first.ResponderSet).ConfigureAwait(false);

        Assert.AreEqual(0, second.DecodedCount);
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(second.InitiatorSet));
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(second.ResponderSet));
    }


    /// <summary>A snapshot responder and incremental initiator converge both replicas over a deterministically paced socket exchange.</summary>
    [TestMethod]
    public async Task ASnapshotResponderConvergesAgainstAnIncrementalInitiatorOverASocket()
    {
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> responderSet = ancestor.Add("zeta", R3);

        SnapshotRoundOutcome result = await RunSnapshotResponderRoundAsync(initiatorSet, responderSet, batchSize: 4).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedConverged, Sorted(result.InitiatorSet));
        Assert.AreSequenceEqual(ExpectedConverged, Sorted(result.ResponderSet));
        Assert.AreEqual(3, result.DecodedCount);
        Assert.AreEqual(AntiEntropySessionState.Completed, result.InitiatorState);
        Assert.IsTrue(result.InitiatorConverged);
        Assert.AreEqual(AntiEntropySessionState.Completed, result.ResponderState);
        Assert.IsTrue(result.ResponderConverged);
        Assert.IsGreaterThanOrEqualTo(1, result.Calls);
        Assert.IsLessThanOrEqualTo(result.Batches, result.Calls);
    }


    /// <summary>
    /// Runs a snapshot responder against an incremental initiator over a fresh duplex socket to convergence,
    /// and returns both sides' sets, terminal states, convergence, decoded count, and responder pass bounds.
    /// </summary>
    /// <remarks>
    /// The listener, client, and server are all disposed in the finally even when the proof fails mid-flight.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, and server are all disposed in the finally block.")]
    [SuppressMessage("Usage", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The initiator run task is awaited directly before the finally, and the responder run and both reader tasks are observed through SwallowAsync in the finally; every session-touching task has completed before the sessions and the linked token source are disposed. The analyzer cannot see completion across the direct await ordering and the helper.")]
    private async Task<SnapshotRoundOutcome> RunSnapshotResponderRoundAsync(OrSet<string> initiatorSet, OrSet<string> responderSet, int batchSize)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;
        Task? initiatorReader = null;
        Task? responderReader = null;
        Task? responderRun = null;
        AntiEntropySession<string>? initiatorSession = null;
        AntiEntropySession<string>? responderSession = null;

        //The timeout -- not a fixed sleep -- bounds the deterministic choreography so a stalled side fails the
        //test instead of hanging the run; the cancellation token is the synchronization boundary.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            server = await acceptTask.ConfigureAwait(false);

            NetworkStream clientStream = client.GetStream();
            NetworkStream serverStream = server.GetStream();

            MessageChannelWriter<ReconciliationEnvelope<string>> initiatorOut = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<string>> initiatorIn = new(PipeReader.Create(clientStream), Deserialize);
            MessageChannelWriter<ReconciliationEnvelope<string>> responderOut = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<string>> responderIn = new(PipeReader.Create(serverStream), Deserialize);

            ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
            ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

            AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, Contract, initiatorItems, BaseMemoryPool.Shared);
            CountingEnumeration counting = new(responderItems);
            AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, Contract, counting.Enumerate, batchSize, BaseMemoryPool.Shared);
            initiatorSession = initiator;
            responderSession = responder;
            int symbols = SymbolsToDecode(Contract, initiatorItems, responderItems);
            int batches = (symbols + batchSize - 1) / batchSize;

            Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
            Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
            HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

            //The initiator partitions the decoded difference into digests it lacks (fetch) and digests it holds
            //in surplus (push); the responder serves fetches from its directory; both sides add received elements
            //under their own replica id.
            ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            {
                List<ReadOnlyMemory<byte>> fetch = [];
                List<ReconciliationElementEntry<string>> push = [];
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

                return new ReconciliationDifferenceResolution<string>([.. fetch], [.. push]);
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

            SendReconciliationEnvelopeDelegate<string> initiatorSend = (envelope, token) => initiatorOut.WriteAsync(envelope, token);
            SendReconciliationEnvelopeDelegate<string> responderSend = (envelope, token) => responderOut.WriteAsync(envelope, token);

            Task initiatorRun = initiator.RunAsync(initiatorSend, resolve, null, applyToInitiator, cancellationToken: cancellationToken);
            responderRun = responder.RunAsync(responderSend, null, serve, applyToResponder, cancellationToken: cancellationToken);

            initiatorReader = ForwardInboundAsync(initiatorIn, initiator, cancellationToken);
            responderReader = ForwardInboundAndTriggerAsync(responderIn, responder, batches, cancellationToken);

            //The initiator completes its single consumer loop on reaching its terminal state; with that done,
            //completing its writer lets the responder's reader loop drain and end, after which the responder is
            //wound down and the choreography is torn down in order.
            await initiatorRun.ConfigureAwait(false);

            await initiatorOut.CompleteAsync().ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);
            await responderReader.ConfigureAwait(false);

            responder.Complete();
            await responderRun.ConfigureAwait(false);

            //No responder-side writer completion or shutdown is needed: the responder reader's completion
            //disposed its stream, which owns the server socket, so the initiator's reader already observes
            //end-of-stream from the socket closure.
            await initiatorReader.ConfigureAwait(false);

            return new SnapshotRoundOutcome(initiatorResult, responderResult, initiator.DecodedItems.Count,
                initiator.State, responder.State, initiator.IsConverged, responder.IsConverged, counting.Calls, batches);
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            listener.Dispose();

            //Observe every background task on all paths so a mid-round failure surfaces its own cause instead
            //of leaking unobserved exceptions: socket disposal above unblocks the readers, completing the
            //responder lets its run loop drain, and the linked timeout bounds everything else.
            responderSession?.Complete();
            await SwallowAsync(responderRun).ConfigureAwait(false);
            await SwallowAsync(responderReader).ConfigureAwait(false);
            await SwallowAsync(initiatorReader).ConfigureAwait(false);

            //Both sessions own pooled cell stores; dispose them only now, after every background task that used
            //them has been observed above, so no reader task touches a released backing.
            initiatorSession?.Dispose();
            responderSession?.Dispose();
        }
    }


    /// <summary>Returns the exact symbol prefix a mirror decoder needs for the supplied snapshots.</summary>
    private static int SymbolsToDecode(ReconciliationContract contract, IReadOnlyList<ReadOnlyMemory<byte>> initiatorItems, IReadOnlyList<ReadOnlyMemory<byte>> responderItems)
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

        return symbols;
    }


    /// <summary>Forwards inbound envelopes and queues the fixed batch triggers immediately after the peer's offer.</summary>
    private static async Task ForwardInboundAndTriggerAsync(MessageChannelReader<ReconciliationEnvelope<string>> inbound, AntiEntropySession<string> responder, int batches, CancellationToken cancellationToken)
    {
        await foreach(ReconciliationEnvelope<string> envelope in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responder.SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
            if(envelope.Offer is not null)
            {
                for(int batch = 0; batch < batches; batch++)
                {
                    await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
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


    /// <summary>The converged sets and terminal session observations with the responder's pass count and trigger bound.</summary>
    private readonly record struct SnapshotRoundOutcome(
        OrSet<string> InitiatorSet,
        OrSet<string> ResponderSet,
        int DecodedCount,
        AntiEntropySessionState InitiatorState,
        AntiEntropySessionState ResponderState,
        bool InitiatorConverged,
        bool ResponderConverged,
        int Calls,
        int Batches);


    /// <summary>
    /// Stands up one fresh duplex socket connection, runs one full initiator/responder session to convergence,
    /// and returns both sides' resulting sets and the initiator's decoded count.
    /// </summary>
    /// <remarks>
    /// The listener, client, and server are all disposed in the finally even when the proof fails mid-flight.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, and server are all disposed in the finally block.")]
    [SuppressMessage("Usage", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The initiator run task is awaited directly before the finally, and the responder run, both reader, and pacing tasks are observed through SwallowAsync in the finally; every session-touching task has completed before the sessions and the linked token source are disposed. The analyzer cannot see completion across the direct await ordering and the helper.")]
    private async Task<RoundOutcome> RunRoundAsync(OrSet<string> initiatorSet, OrSet<string> responderSet)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;
        Task? initiatorReader = null;
        Task? responderReader = null;
        Task? responderRun = null;
        Task? pacing = null;
        AntiEntropySession<string>? initiatorSession = null;
        AntiEntropySession<string>? responderSession = null;

        //The timeout -- not a fixed sleep -- bounds the deterministic choreography so a stalled side fails the
        //test instead of hanging the run; the cancellation token is the synchronization boundary.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            server = await acceptTask.ConfigureAwait(false);

            NetworkStream clientStream = client.GetStream();
            NetworkStream serverStream = server.GetStream();

            MessageChannelWriter<ReconciliationEnvelope<string>> initiatorOut = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<string>> initiatorIn = new(PipeReader.Create(clientStream), Deserialize);
            MessageChannelWriter<ReconciliationEnvelope<string>> responderOut = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<string>> responderIn = new(PipeReader.Create(serverStream), Deserialize);

            ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
            ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

            AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, Contract, initiatorItems, BaseMemoryPool.Shared);
            AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, Contract, responderItems, BaseMemoryPool.Shared);
            initiatorSession = initiator;
            responderSession = responder;

            Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
            Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
            HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

            //The initiator partitions the decoded difference into digests it lacks (fetch) and digests it holds
            //in surplus (push); the responder serves fetches from its directory; both sides add received elements
            //under their own replica id.
            ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            {
                List<ReadOnlyMemory<byte>> fetch = [];
                List<ReconciliationElementEntry<string>> push = [];
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

                return new ReconciliationDifferenceResolution<string>([.. fetch], [.. push]);
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

            SendReconciliationEnvelopeDelegate<string> initiatorSend = (envelope, token) => initiatorOut.WriteAsync(envelope, token);
            SendReconciliationEnvelopeDelegate<string> responderSend = (envelope, token) => responderOut.WriteAsync(envelope, token);

            Task initiatorRun = initiator.RunAsync(initiatorSend, resolve, null, applyToInitiator, cancellationToken: cancellationToken);
            responderRun = responder.RunAsync(responderSend, null, serve, applyToResponder, cancellationToken: cancellationToken);

            initiatorReader = ForwardInboundAsync(initiatorIn, initiator, cancellationToken);
            responderReader = ForwardInboundAsync(responderIn, responder, cancellationToken);

            pacing = PaceResponderAsync(responder, cancellationToken);

            //The initiator completes its single consumer loop on reaching its terminal state; with that done,
            //completing its writer lets the responder's reader loop drain and end, after which the responder is
            //wound down and the choreography is torn down in order.
            await initiatorRun.ConfigureAwait(false);
            await pacing.ConfigureAwait(false);

            await initiatorOut.CompleteAsync().ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);
            await responderReader.ConfigureAwait(false);

            responder.Complete();
            await responderRun.ConfigureAwait(false);

            //No responder-side writer completion or shutdown is needed: the responder reader's completion
            //disposed its stream, which owns the server socket, so the initiator's reader already observes
            //end-of-stream from the socket closure.
            await initiatorReader.ConfigureAwait(false);

            return new RoundOutcome(initiatorResult, responderResult, initiator.DecodedItems.Count);
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            listener.Dispose();

            //Observe every background task on all paths so a mid-round failure surfaces its own cause instead
            //of leaking unobserved exceptions: socket disposal above unblocks the readers, completing the
            //responder lets its run loop drain, and the linked timeout bounds everything else.
            responderSession?.Complete();
            await SwallowAsync(pacing).ConfigureAwait(false);
            await SwallowAsync(responderRun).ConfigureAwait(false);
            await SwallowAsync(responderReader).ConfigureAwait(false);
            await SwallowAsync(initiatorReader).ConfigureAwait(false);

            //Both sessions own pooled cell stores; dispose them only now, after every background task that used
            //them has been observed above, so no reader or pacing task touches a released backing.
            initiatorSession?.Dispose();
            responderSession?.Dispose();
        }
    }


    /// <summary>
    /// Awaits a wind-down task, swallowing the failure shapes that socket disposal and cancellation induce so
    /// the original round failure stays the surfaced cause.
    /// </summary>
    private static async Task SwallowAsync(Task? task)
    {
        if(task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch(Exception exception) when(exception is OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException or SocketException)
        {
        }
    }


    /// <summary>
    /// Reads framed envelopes off one inbound channel and forwards each to the owning session's producer queue;
    /// the loop ends when the peer completes its writer.
    /// </summary>
    private static async Task ForwardInboundAsync(MessageChannelReader<ReconciliationEnvelope<string>> inbound, AntiEntropySession<string> session, CancellationToken cancellationToken)
    {
        await foreach(ReconciliationEnvelope<string> envelope in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await session.SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Triggers responder batches under a short delay until its state leaves Reconciling -- the done signal moves
    /// it to Resolving -- capping the loop so a never-advancing peer fails the test instead of spinning forever.
    /// </summary>
    private static async Task PaceResponderAsync(AntiEntropySession<string> responder, CancellationToken cancellationToken)
    {
        int triggers = 0;
        while(responder.State == AntiEntropySessionState.Created
            || responder.State == AntiEntropySessionState.Pinning
            || responder.State == AntiEntropySessionState.Reconciling)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            triggers++;
            Assert.IsLessThan(TriggerCap, triggers, "The responder never left Reconciling within the trigger cap.");
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


    private readonly record struct RoundOutcome(OrSet<string> InitiatorSet, OrSet<string> ResponderSet, int DecodedCount);
}
