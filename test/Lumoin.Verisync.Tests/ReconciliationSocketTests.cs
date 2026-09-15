using Lumoin.Base;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// End-to-end reconciliation over a real localhost socket: a <see cref="TcpListener"/> on the loopback
/// ephemeral port, one duplex <see cref="TcpClient"/> connection, and pipe-backed message channels with the
/// <see cref="ReconciliationJson"/> codecs. Both readers leave the shared duplex stream open, so no read loop
/// can dispose a connection its peer is still writing into; the session ends on a half-shutdown of the
/// initiator's send side, as <see cref="LogCommitmentSocketTests"/> ends its own, and the connection is closed
/// once the round is over. Each round is its own fresh session over its own connection; per-direction messages
/// are handled in arrival order by a single reader loop per side, which is what makes the post-completion
/// fetch/elements exchange sound.
/// </summary>
/// <remarks>
/// Divergence is ADD-ONLY. Remove-aware reconciliation needs the causal/frontier machinery of the phase 3
/// session runner; this proof pins element-level convergence only: an ancestor of alpha, beta, gamma; side A
/// (the client) additionally holds delta, epsilon; side B (the server) additionally holds zeta. The true
/// difference is three digests, and a second fresh session over the converged sets proves quiescence.
/// </remarks>
[TestClass]
internal sealed class ReconciliationSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int BatchSize = 4;

    private const int StreamCap = 200;

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
    public async Task TwoRoundReconciliationConvergesThenReachesQuiescenceOverASocket()
    {
        await RunTwoRoundProofAsync(padding: null).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task TwoRoundReconciliationConvergesThenReachesQuiescenceOverAPaddedSocket()
    {
        await RunTwoRoundProofAsync(FramePadding.PowersOfTwo(256)).ConfigureAwait(false);
    }


    private async Task RunTwoRoundProofAsync(FramePadding? padding)
    {
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> sideA = ancestor.Add("delta", R2).Add("epsilon", R2);
        OrSet<string> sideB = ancestor.Add("zeta", R3);

        string[] expectedConverged = ExpectedConverged;

        //Round one: A decodes the three-digest difference, partitions it into what it lacks and what it holds
        //in surplus, and the two sides exchange the missing elements so both converge.
        RoundOutcome first = await RunRoundAsync(sideA, sideB, padding).ConfigureAwait(false);

        string[] expectedLacks = [Hex(Digest("zeta").Span)];
        string[] expectedSurplus = [.. new[] { Hex(Digest("delta").Span), Hex(Digest("epsilon").Span) }.Order(StringComparer.Ordinal)];
        Assert.AreSequenceEqual(expectedLacks, first.Lacks);
        Assert.AreSequenceEqual(expectedSurplus, first.Surplus);

        Assert.AreSequenceEqual(expectedConverged, Sorted(first.SideA));
        Assert.AreSequenceEqual(expectedConverged, Sorted(first.SideB));

        //Round two: a fresh session over the converged sets must reach quiescence. A's decoder is complete
        //after exactly one absorbed symbol with no decoded items, so A sends done(1) and nothing else flows.
        RoundOutcome second = await RunRoundAsync(first.SideA, first.SideB, padding).ConfigureAwait(false);

        Assert.AreEqual(1, second.AbsorbedCount);
        Assert.AreEqual(0, second.DecodedCount);
        Assert.HasCount(0, second.Lacks);
        Assert.HasCount(0, second.Surplus);
        Assert.AreSequenceEqual(expectedConverged, Sorted(second.SideA));
        Assert.AreSequenceEqual(expectedConverged, Sorted(second.SideB));
    }


    /// <summary>
    /// Stands up one fresh duplex socket connection, runs the initiator and responder choreography to
    /// completion, and returns the round's observed metrics and both sides' converged sets.
    /// </summary>
    /// <remarks>
    /// The session ends on an ordering barrier rather than a race: the initiator's task completes first, only
    /// then is its send side half-closed, and that end of stream is the one event the responder's loop ends on.
    /// The listener, client, and server are all disposed in the finally even when the proof fails mid-flight.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, server, and linked token source are all disposed in the finally block.")]
    [SuppressMessage("Usage", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The initiator task is awaited before the barrier and the responder task is observed through SwallowAsync in the finally, so every task touching the connection has completed before the linked token source is disposed.")]
    private async Task<RoundOutcome> RunRoundAsync(OrSet<string> sideA, OrSet<string> sideB, FramePadding? padding)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;
        Task<OrSet<string>>? responder = null;

        //The timeout — not a fixed sleep — bounds the deterministic choreography so a stalled side fails the
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

            //Both readers leave the duplex stream open, so a read loop that ends never disposes the connection
            //under a peer that is still writing; the connection is closed once, in the finally, after both
            //sides have returned.
            MessageChannelWriter<ReconciliationEnvelope<string>> aOut = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize, padding: padding);
            MessageChannelReader<ReconciliationEnvelope<string>> aIn = new(PipeReader.Create(clientStream, new StreamPipeReaderOptions(leaveOpen: true)), Deserialize, padding: padding);
            MessageChannelWriter<ReconciliationEnvelope<string>> bOut = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), Serialize, padding: padding);
            MessageChannelReader<ReconciliationEnvelope<string>> bIn = new(PipeReader.Create(serverStream, new StreamPipeReaderOptions(leaveOpen: true)), Deserialize, padding: padding);

            Task<InitiatorResult> initiator = RunInitiatorAsync(sideA, aOut, aIn, cancellationToken);
            responder = RunResponderAsync(sideB, bOut, bIn, cancellationToken);

            //The initiator finishes its exchange first; only then is its send side half-closed. An end of
            //stream, unlike a reset, is ordered behind every frame already sent, so the close cannot destroy a
            //frame the responder has not yet read, and the responder writes into a live connection until it has
            //returned.
            InitiatorResult aResult = await initiator.ConfigureAwait(false);
            await aOut.CompleteAsync().ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);

            OrSet<string> bResult = await responder.ConfigureAwait(false);

            return new RoundOutcome(aResult.Lacks, aResult.Surplus, aResult.AbsorbedCount, aResult.DecodedCount, aResult.Set, bResult);
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            listener.Dispose();

            //A round that fails before the barrier leaves the responder waiting on an end of stream that never
            //comes; the disposal above unblocks it, and its wind-down failure is reported rather than thrown so
            //the round's original cause stays the surfaced one.
            await SwallowAsync(responder).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Awaits a wind-down task that only matters once the round has already failed, reporting rather than
    /// throwing its failure so the round's original cause stays the surfaced one.
    /// </summary>
    /// <param name="task">The wind-down task, or <see langword="null"/> when the round failed before it started.</param>
    /// <returns>A task that completes when the wind-down task has been observed.</returns>
    private async Task SwallowAsync(Task? task)
    {
        if(task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch(Exception exception) when(exception is OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException or SocketException or MessageDeserializationException or AssertFailedException)
        {
            TestContext.WriteLine($"The responder wound down with {exception.GetType().Name}: {exception.Message}");
        }
    }


    /// <summary>
    /// The initiator pins its offer, absorbs the responder's streamed symbols against its own fresh encoder
    /// until the decoder completes, then resolves the difference: it requests the digests it lacks and offers
    /// the elements it holds in surplus, applies the responder's answer, and ends its single reader loop.
    /// </summary>
    /// <remarks>
    /// This side does not complete its own writer: the caller completes it and half-closes the send side once
    /// this task has returned, which is what ends the responder's loop.
    /// </remarks>
    private static async Task<InitiatorResult> RunInitiatorAsync(
        OrSet<string> set,
        MessageChannelWriter<ReconciliationEnvelope<string>> outbound,
        MessageChannelReader<ReconciliationEnvelope<string>> inbound,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>[] items = Project(set);
        Dictionary<string, string> directory = BuildDirectory(set);
        HashSet<string> localHexes = [.. items.Select(item => Hex(item.Span))];

        using ReconciliationEncoder localEncoder = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            localEncoder.Add(item.Span);
        }

        using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);

        await outbound.WriteAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(Contract)), cancellationToken).ConfigureAwait(false);

        bool completed = false;
        bool awaitingAnswer = false;
        string[] lacks = [];
        string[] surplus = [];
        int decodedCount = 0;

        await foreach(ReconciliationEnvelope<string> envelope in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if(envelope.Symbols is not null && !completed)
            {
                ReconciliationSymbolBatch batch = envelope.Symbols;

                //In-order, gap-free streaming: a batch must begin exactly where this side's absorbed count stands.
                Assert.AreEqual(decoder.AbsorbedCount, batch.StartIndex);

                foreach(ReconciliationSymbol remote in batch.Symbols)
                {
                    decoder.Absorb(localEncoder.ProduceNext().Combine(remote));
                    if(decoder.IsComplete)
                    {
                        completed = true;
                        decodedCount = decoder.DecodedItems.Count;
                        await outbound.WriteAsync(ReconciliationEnvelope<string>.ForDone(new ReconciliationDone(decoder.AbsorbedCount)), cancellationToken).ConfigureAwait(false);

                        (lacks, surplus) = Partition(decoder.DecodedItems, localHexes);
                        if(lacks.Length > 0)
                        {
                            awaitingAnswer = true;
                            await outbound.WriteAsync(ReconciliationEnvelope<string>.ForFetch(BuildFetch(lacks)), cancellationToken).ConfigureAwait(false);
                        }

                        if(surplus.Length > 0)
                        {
                            await outbound.WriteAsync(ReconciliationEnvelope<string>.ForElements(BuildElements(surplus, directory)), cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    }
                }

                if(completed && !awaitingAnswer)
                {
                    break;
                }
            }
            else if(envelope.Elements is not null && awaitingAnswer)
            {
                foreach(ReconciliationElementEntry<string> entry in envelope.Elements.Entries)
                {
                    set = set.Add(entry.Element, R2);
                }

                break;
            }
        }

        return new InitiatorResult(lacks, surplus, decoder.AbsorbedCount, decodedCount, set);
    }


    /// <summary>
    /// The responder pins its offer, streams symbol batches from a fresh encoder over its own projection until
    /// the initiator signals done, answers any fetch from its directory, and applies any surplus elements the
    /// initiator offered.
    /// </summary>
    /// <remarks>
    /// Streaming runs alongside the single reader loop; the loop ends on the initiator's half-close, after
    /// which this side stops the streamer, waits it out, and completes its own writer.
    /// </remarks>
    private static async Task<OrSet<string>> RunResponderAsync(
        OrSet<string> set,
        MessageChannelWriter<ReconciliationEnvelope<string>> outbound,
        MessageChannelReader<ReconciliationEnvelope<string>> inbound,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>[] items = Project(set);
        Dictionary<string, string> directory = BuildDirectory(set);

        await outbound.WriteAsync(ReconciliationEnvelope<string>.ForOffer(ReconciliationOffer.FromContract(Contract)), cancellationToken).ConfigureAwait(false);

        var doneSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task streamer = StreamSymbolsAsync(items, outbound, doneSignal.Task, cancellationToken);

        try
        {
            await foreach(ReconciliationEnvelope<string> envelope in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if(envelope.Done is not null)
                {
                    //Stop the streamer and wait it out before handling anything further: the streamer and this
                    //loop share one channel writer, which is not safe for concurrent writes, so the fetch answer
                    //must never overlap an in-flight batch write.
                    doneSignal.TrySetResult();
                    await streamer.ConfigureAwait(false);
                }
                else if(envelope.Fetch is not null)
                {
                    ReconciliationElements<string> answer = BuildElements([.. envelope.Fetch.Items.Select(item => Hex(item.Span))], directory);
                    await outbound.WriteAsync(ReconciliationEnvelope<string>.ForElements(answer), cancellationToken).ConfigureAwait(false);
                }
                else if(envelope.Elements is not null)
                {
                    foreach(ReconciliationElementEntry<string> entry in envelope.Elements.Entries)
                    {
                        set = set.Add(entry.Element, R3);
                    }
                }
            }
        }
        finally
        {
            //A loop ending for any reason must release the streamer from the shared writer.
            doneSignal.TrySetResult();
        }

        //The loop ended on the initiator's half-close, which follows every frame the initiator sent; the
        //streamer is stopped and waited out so no batch is left in flight past this side's own end.
        await streamer.ConfigureAwait(false);
        await outbound.CompleteAsync().ConfigureAwait(false);

        return set;
    }


    /// <summary>
    /// Streams consecutive symbol batches of a fixed size from a fresh encoder over the projected items,
    /// checking the done signal between batches and capping the stream so a never-completing peer fails the
    /// test.
    /// </summary>
    private static async Task StreamSymbolsAsync(
        ReadOnlyMemory<byte>[] items,
        MessageChannelWriter<ReconciliationEnvelope<string>> outbound,
        Task doneSignal,
        CancellationToken cancellationToken)
    {
        using ReconciliationEncoder encoder = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            encoder.Add(item.Span);
        }

        int produced = 0;
        while(!doneSignal.IsCompleted && produced < StreamCap)
        {
            var symbols = new ReconciliationSymbol[BatchSize];
            for(int i = 0; i < BatchSize; i++)
            {
                symbols[i] = encoder.ProduceNext();
            }

            ReconciliationSymbolBatch batch = new(produced, [.. symbols]);
            produced += BatchSize;

            await outbound.WriteAsync(ReconciliationEnvelope<string>.ForSymbols(batch), cancellationToken).ConfigureAwait(false);

            //Pace the stream so the initiator's done signal can round-trip before the cap; the signal ends
            //the wait early, so a completed peer never waits out the delay.
            await Task.WhenAny(doneSignal, Task.Delay(10, cancellationToken)).ConfigureAwait(false);
        }

        Assert.IsLessThan(StreamCap, produced, "The responder hit the stream cap without the initiator completing.");
    }


    /// <summary>
    /// Splits the decoded difference into the digests this side does not hold locally (to be fetched) and the
    /// digests it does hold (a surplus the peer lacks), both as sorted uppercase hex sets.
    /// </summary>
    private static (string[] Lacks, string[] Surplus) Partition(IReadOnlyList<ReadOnlyMemory<byte>> decoded, HashSet<string> localHexes)
    {
        var lacks = new List<string>();
        var surplus = new List<string>();
        foreach(ReadOnlyMemory<byte> item in decoded)
        {
            string hex = Hex(item.Span);
            if(localHexes.Contains(hex))
            {
                surplus.Add(hex);
            }
            else
            {
                lacks.Add(hex);
            }
        }

        lacks.Sort(StringComparer.Ordinal);
        surplus.Sort(StringComparer.Ordinal);

        return ([.. lacks], [.. surplus]);
    }


    private static ReconciliationFetch BuildFetch(string[] hexes)
    {
        return new ReconciliationFetch([.. hexes.Select(hex => (ReadOnlyMemory<byte>)Convert.FromHexString(hex))]);
    }


    private static ReconciliationElements<string> BuildElements(string[] hexes, Dictionary<string, string> directory)
    {
        return new ReconciliationElements<string>([.. hexes.Select(hex => new ReconciliationElementEntry<string>(Convert.FromHexString(hex), directory[hex]))]);
    }


    private static Dictionary<string, string> BuildDirectory(OrSet<string> set)
    {
        var directory = new Dictionary<string, string>();
        foreach(string element in set.Elements)
        {
            directory[Hex(Digest(element).Span)] = element;
        }

        return directory;
    }


    private static ReadOnlyMemory<byte>[] Project(OrSet<string> set)
    {
        ProjectReconciliationItemsDelegate<OrSet<string>> project = static (state, _) =>
        {
            List<ReadOnlyMemory<byte>> projected = [];
            foreach(string element in state.Elements)
            {
                projected.Add(Digest(element));
            }

            return projected;
        };

        return [.. project(set, Contract)];
    }


    private static string[] Sorted(OrSet<string> set)
    {
        return [.. set.Elements.Order()];
    }


    private static ReadOnlyMemory<byte> Digest(string element)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(element));
    }


    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private readonly record struct RoundOutcome(string[] Lacks, string[] Surplus, int AbsorbedCount, int DecodedCount, OrSet<string> SideA, OrSet<string> SideB);


    private readonly record struct InitiatorResult(string[] Lacks, string[] Surplus, int AbsorbedCount, int DecodedCount, OrSet<string> Set);
}
