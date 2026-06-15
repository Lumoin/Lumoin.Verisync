using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// End-to-end proof that REMOVE-AWARE (dot-cloud) reconciliation converges over a real localhost socket, not
/// only in-process. It is the missing companion to <see cref="AntiEntropySocketTests"/> and
/// <see cref="ReconciliationSocketTests"/> (which prove the add-only path over a socket) and to
/// <see cref="RemoveAwareReconciliationLawTests"/> (which proves the merge law in-process with direct
/// <see cref="AntiEntropySession{TElement}.SubmitAsync"/>, never serializing). Two diverged
/// <see cref="DottedVersionVectorSet{T}"/> snapshots reconcile through the full phase-3 session over a duplex
/// TCP connection, so the new causal-context and drop frames — and the per-entry <see cref="DottedEntry{T}"/>
/// element payload — all cross the <see cref="ReconciliationJson"/> codec and the framed transport. Both
/// replicas must reach <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/>, including the
/// resurrection guard: an entry the initiator observed-and-removed while the responder still holds it must not
/// come back. A frame census on the send delegates asserts the context and drop frames actually crossed the
/// wire, so the proof cannot pass through the add-only degeneracy. The classification host is the shared
/// <see cref="RemoveAwareReconciliationHost"/>, identical to the law tests; the difference under test here is
/// the serializing transport.
/// </summary>
[TestClass]
internal sealed class RemoveAwareReconciliationSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int TriggerCap = 100;

    private const int BatchSize = 4;

    private const string ResurrectionProbe = "ghost";

    private static ReconciliationContract Contract { get; } = ReconciliationContract.ContentHashDefault;

    //The element is a dotted entry, so the wire codec ships its replica, counter, and value; the remove-aware
    //session carries dotted entries (not bare values) precisely because the dot is what the merge classifies.
    private static SerializeMessageDelegate<ReconciliationEnvelope<DottedEntry<string>>> Serialize { get; } =
        ReconciliationJson.CreateEnvelopeSerializer<DottedEntry<string>>(WriteEntry);

    private static DeserializeMessageDelegate<ReconciliationEnvelope<DottedEntry<string>>> Deserialize { get; } =
        ReconciliationJson.CreateEnvelopeDeserializer<DottedEntry<string>>(Contract, ReadEntry);

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task MixedRemoveAwareDivergenceConvergesToTheMergeOverASocket()
    {
        //A mixed divergence with the resurrection probe: the responder mints and keeps "ghost" under R3, the
        //initiator observes that exact dot then removes it and adds "delta", and the responder adds "zeta". The
        //causal merge drops the ghost on both sides, so neither replica may resurrect it across the wire.
        DottedVersionVectorSet<string> ancestor = DottedVersionVectorSet<string>.Empty.Add(R1, "alpha").Add(R1, "beta").Add(R1, "gamma");
        DottedVersionVectorSet<string> withProbe = ancestor.Add(R3, ResurrectionProbe);
        DottedVersionVectorSet<string> initiatorStart = withProbe.Add(R2, "delta").RemoveValue(ResurrectionProbe);
        DottedVersionVectorSet<string> responderStart = withProbe.Add(R3, "zeta");

        DottedVersionVectorSet<string> expected = initiatorStart.Merge(responderStart);
        Assert.DoesNotContain(ResurrectionProbe, expected.Values);

        RoundOutcome outcome = await RunRoundAsync(initiatorStart, responderStart).ConfigureAwait(false);

        Assert.AreEqual(expected, outcome.Initiator);
        Assert.AreEqual(expected, outcome.Responder);
        Assert.DoesNotContain(ResurrectionProbe, outcome.Initiator.Values);
        Assert.DoesNotContain(ResurrectionProbe, outcome.Responder.Values);

        //The remove-aware frames really crossed the serialized socket — this did not collapse to the add-only
        //path: both sides shipped their causal context, and at least one drop frame carried a removed dot.
        Assert.IsGreaterThan(0, outcome.ContextFramesCrossed, "No causal-context frame crossed; the session ran add-only.");
        Assert.IsGreaterThan(0, outcome.DropFramesCrossed, "No drop frame crossed; the observed remove never propagated over the wire.");
    }


    //Stands up one fresh duplex loopback connection, runs one full remove-aware initiator/responder session to
    //convergence with every frame serialized through the codec, and returns both converged sets plus the
    //context and drop frame counts the send delegates observed. The listener, client, and server are disposed in
    //the finally even when the proof fails mid-flight; the wind-down mirrors AntiEntropySocketTests exactly.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, and server are all disposed in the finally block.")]
    [SuppressMessage("Usage", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The initiator run task is awaited before the finally, and the responder run, both reader, and pacing tasks are observed through SwallowAsync in the finally; every session-touching task has completed before the sessions and the linked token source are disposed.")]
    private async Task<RoundOutcome> RunRoundAsync(DottedVersionVectorSet<string> initiatorStart, DottedVersionVectorSet<string> responderStart)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;
        Task? initiatorReader = null;
        Task? responderReader = null;
        Task? responderRun = null;
        Task? pacing = null;
        AntiEntropySession<DottedEntry<string>>? initiatorSession = null;
        AntiEntropySession<DottedEntry<string>>? responderSession = null;

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

            MessageChannelWriter<ReconciliationEnvelope<DottedEntry<string>>> initiatorOut = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<DottedEntry<string>>> initiatorIn = new(PipeReader.Create(clientStream), Deserialize);
            MessageChannelWriter<ReconciliationEnvelope<DottedEntry<string>>> responderOut = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize);
            MessageChannelReader<ReconciliationEnvelope<DottedEntry<string>>> responderIn = new(PipeReader.Create(serverStream), Deserialize);

            RemoveAwareReconciliationHost initiatorHost = new(initiatorStart);
            RemoveAwareReconciliationHost responderHost = new(responderStart);

            //Both sessions are remove-aware: the optional local context pins each replica's causal frontier so the
            //session ships it after the offer and the host can classify decoded dots against the peer's.
            AntiEntropySession<DottedEntry<string>> initiator = new(AntiEntropyRole.Initiator, Contract, initiatorHost.Items, BatchSize, null, localContext: initiatorHost.LocalContext);
            AntiEntropySession<DottedEntry<string>> responder = new(AntiEntropyRole.Responder, Contract, responderHost.Items, BatchSize, null, localContext: responderHost.LocalContext);
            initiatorSession = initiator;
            responderSession = responder;

            FrameCensus census = new();
            SendReconciliationEnvelopeDelegate<DottedEntry<string>> initiatorSend = census.Wrap(initiatorOut);
            SendReconciliationEnvelopeDelegate<DottedEntry<string>> responderSend = census.Wrap(responderOut);

            Task initiatorRun = initiator.RunAsync(
                initiatorSend,
                initiatorHost.ResolveDifference,
                null,
                initiatorHost.ApplyElements,
                applyDrops: initiatorHost.ApplyDrops,
                mergeContext: initiatorHost.MergeContext,
                cancellationToken: cancellationToken);

            responderRun = responder.RunAsync(
                responderSend,
                null,
                responderHost.ServeFetch,
                responderHost.ApplyElements,
                applyDrops: responderHost.ApplyDrops,
                mergeContext: responderHost.MergeContext,
                cancellationToken: cancellationToken);

            initiatorReader = ForwardInboundAsync(initiatorIn, initiator, cancellationToken);
            responderReader = ForwardInboundAsync(responderIn, responder, cancellationToken);

            pacing = PaceResponderAsync(responder, cancellationToken);

            await initiatorRun.ConfigureAwait(false);
            await pacing.ConfigureAwait(false);

            await initiatorOut.CompleteAsync().ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);
            await responderReader.ConfigureAwait(false);

            responder.Complete();
            await responderRun.ConfigureAwait(false);

            await initiatorReader.ConfigureAwait(false);

            return new RoundOutcome(initiatorHost.Current, responderHost.Current, census.ContextFrames, census.DropFrames);
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            listener.Dispose();

            responderSession?.Complete();
            await SwallowAsync(pacing).ConfigureAwait(false);
            await SwallowAsync(responderRun).ConfigureAwait(false);
            await SwallowAsync(responderReader).ConfigureAwait(false);
            await SwallowAsync(initiatorReader).ConfigureAwait(false);

            initiatorSession?.Dispose();
            responderSession?.Dispose();
        }
    }


    //Awaits a wind-down task, swallowing the failure shapes that socket disposal and cancellation induce so the
    //original round failure stays the surfaced cause.
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
        catch(Exception exception) when(exception is OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException or SocketException or MessageDeserializationException)
        {
        }
    }


    //Reads framed envelopes off one inbound channel and forwards each to the owning session's producer queue;
    //the loop ends when the peer completes its writer.
    private static async Task ForwardInboundAsync(MessageChannelReader<ReconciliationEnvelope<DottedEntry<string>>> inbound, AntiEntropySession<DottedEntry<string>> session, CancellationToken cancellationToken)
    {
        await foreach(ReconciliationEnvelope<DottedEntry<string>> envelope in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await session.SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }


    //Triggers responder batches under a short delay until its state leaves Reconciling, capping the loop so a
    //never-advancing peer fails the test instead of spinning forever.
    private static async Task PaceResponderAsync(AntiEntropySession<DottedEntry<string>> responder, CancellationToken cancellationToken)
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


    //Writes a dotted entry as its replica hex, counter, and value; the deserializer reads it back and rebuilds
    //the entry, so the per-entry payload round-trips through the codec exactly as the fetch/elements frames need.
    private static void WriteEntry(Utf8JsonWriter writer, DottedEntry<string> entry)
    {
        writer.WriteStartObject();
        writer.WriteString("replica", Convert.ToHexStringLower(entry.Replica.AsSpan()));
        writer.WriteNumber("counter", entry.Counter);
        writer.WriteString("value", entry.Value);
        writer.WriteEndObject();
    }


    private static DottedEntry<string> ReadEntry(JsonElement element)
    {
        ImmutableArray<byte> replica = ImmutableArray.Create(Convert.FromHexString(element.GetProperty("replica").GetString()!));
        int counter = element.GetProperty("counter").GetInt32();
        string value = element.GetProperty("value").GetString()!;

        return new DottedEntry<string>(replica, counter, value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private readonly record struct RoundOutcome(DottedVersionVectorSet<string> Initiator, DottedVersionVectorSet<string> Responder, int ContextFramesCrossed, int DropFramesCrossed);


    //Counts the causal-context and drop frames a wrapped send delegate carries before forwarding to the
    //channel writer, so the test can prove the remove-aware payloads actually crossed the serialized socket. The
    //session serializes its sends through the single consumer loop, but both sides share one census, so the
    //counters move under Interlocked.
    private sealed class FrameCensus
    {
        private int contextFrames;
        private int dropFrames;


        public int ContextFrames => Volatile.Read(ref contextFrames);

        public int DropFrames => Volatile.Read(ref dropFrames);


        public SendReconciliationEnvelopeDelegate<DottedEntry<string>> Wrap(MessageChannelWriter<ReconciliationEnvelope<DottedEntry<string>>> writer)
        {
            return (envelope, cancellationToken) =>
            {
                if(envelope.Context is not null)
                {
                    Interlocked.Increment(ref contextFrames);
                }

                if(envelope.Drop is not null)
                {
                    Interlocked.Increment(ref dropFrames);
                }

                return writer.WriteAsync(envelope, cancellationToken);
            };
        }
    }
}
