using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A cluster of QuePaxa recorder hosts for a versioned register, each served by its own runner behind a
/// loopback TCP connection, so a register scenario runs against real framing, real serialization and real
/// per-call correlation.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// It sits beside <see cref="VersionedQuePaxaCluster{TValue}"/> with the same observations,
/// <see cref="Served"/>, <see cref="Declined"/> and <see cref="Recorded"/>, plus <see cref="Sent"/>, which
/// only a wire bench needs, and the same <see cref="Partition"/> and <see cref="Heal"/>. The controls the
/// runner's ownership forces to differ are named for it: learns are <see cref="LearnAtAsync"/>,
/// <see cref="LearnInMemoryAtAsync"/> and
/// <see cref="LearnAllAsync"/> because they queue through the runner, a catch-up read is served by
/// <see cref="Readers"/> over the wire because nothing may touch a node the runner owns,
/// <see cref="DrainAsync"/> is the barrier the wire adds, and reply corruption is injected as bytes at
/// connect time rather than by wrapping an endpoint. The point of the pair stands: the in-memory bench pins
/// what the protocol does, and this one pins that the wire does not change it.
/// </para>
/// <para>
/// Dissemination is explicit here because it is explicit in a deployment. A host learns a committed record
/// only when a test calls <see cref="LearnAtAsync"/> or <see cref="LearnAllAsync"/>, or wires
/// <see cref="PublishAsync"/> as a register's publish so the record crosses the wire into the host's own
/// receive leg, which is what lets a test hold hosts back and watch a write fail to gather a quorum.
/// </para>
/// <para>
/// An observation a serve loop writes is complete only after <see cref="DrainAsync"/>, unless the test can
/// show no frame can yet be in flight: a clock that has not advanced, or an outcome only a served frame
/// could have produced. <see cref="Served"/>, <see cref="Declined"/>, <see cref="Recorded"/> and
/// <see cref="Answered"/> are written
/// by the serve loops, and a write's outcome is no barrier for a frame still in flight, because a proposer
/// that abandoned a slow recorder leaves a request the host has yet to answer. A test therefore drains
/// first and asserts afterwards, and the tests that take the exemption carry the argument for it at the
/// assertion. <see cref="Sent"/> is exempt by construction.
/// </para>
/// <para>
/// The runner is the only sequenced path to a host. Nothing here touches a node after its loop starts,
/// because the node's ownership latch throws for anyone but its runner.
/// </para>
/// </remarks>
internal sealed class SocketVersionedQuePaxaCluster<TValue>: IAsyncDisposable
{
    private const byte RecordKind = 0;
    private const byte ReadKind = 1;
    private const byte LearnKind = 2;
    private const byte VersionKind = 3;

    private static byte[] JsonNull { get; } = "null"u8.ToArray();
    private static byte[] JsonTrue { get; } = "true"u8.ToArray();
    private static byte[] JsonFalse { get; } = "false"u8.ToArray();


    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The channels, gates and enumerators built here are held in this instance's arrays and disposed by DisposeAsync.")]
    private SocketVersionedQuePaxaCluster(
        QuePaxaLeaderSchedule schedule,
        QuePaxaConfiguration? genesis,
        WriteValueDelegate<Utf8JsonWriter, TValue> writeValue,
        ReadValueDelegate<JsonElement, TValue> readValue,
        VersionedValue<TValue>? committed,
        TamperReplyPayloadDelegate? tamperReplyPayload,
        PersistVersionedNodeDelegate<TValue>[]? persistNodes,
        TcpListener[] listeners,
        TcpClient[] clients,
        TcpClient[] servers,
        CancellationToken cancellationToken)
    {
        int hostCount = listeners.Length;

        Schedule = schedule;
        Genesis = genesis ?? QuePaxaConfiguration.CreateGenesis(schedule.Schedule.Order);
        TamperReplyPayload = tamperReplyPayload;
        Listeners = listeners;
        Clients = clients;
        Servers = servers;

        WriteRecordValue = QuePaxaMessageJson.CreateVersionedValueWriter(writeValue);
        ReadRecordValue = QuePaxaMessageJson.CreateVersionedValueReader(readValue);
        RequestSerialize = QuePaxaMessageJson.CreateVersionedRequestSerializer(WriteRecordValue);
        RequestDeserialize = QuePaxaMessageJson.CreateVersionedRequestDeserializer(ReadRecordValue);
        ReplySerialize = QuePaxaMessageJson.CreateVersionedReplySerializer(WriteRecordValue);
        ReplyDeserialize = QuePaxaMessageJson.CreateVersionedReplyDeserializer(ReadRecordValue);

        Runners = new QuePaxaVersionedRunner<TValue>[hostCount];
        Receives = new ReceiveCommittedRecordDelegate<TValue>[hostCount];
        RunTasks = new Task[hostCount];
        ServingTasks = new Task[hostCount];
        RequestWriters = new MessageChannelWriter<CorrelatedFrame>[hostCount];
        ReplyReaders = new MessageChannelReader<CorrelatedFrame>[hostCount];
        ReplyEnumerators = new IAsyncEnumerator<CorrelatedFrame>[hostCount];
        Gates = new SemaphoreSlim[hostCount];
        GateTaken = new bool[hostCount];
        NextIds = new int[hostCount];
        Partitioned = new bool[hostCount];
        ServedCounts = new int[hostCount];
        DisseminatedCounts = new int[hostCount];
        SentCounts = new int[hostCount];

        for(int index = 0; index < hostCount; index++)
        {
            Runners[index] = new QuePaxaVersionedRunner<TValue>(new QuePaxaVersionedNode<TValue>(Genesis, schedule.Schedule.Order[index], committed));

            //The assignment is the conversion the runner's contract names: LearnAsync is this host's
            //ReceiveCommittedRecordDelegate, and the serve loop offers every wire-borne record through it.
            Receives[index] = Runners[index].LearnAsync;
            RunTasks[index] = Runners[index].RunAsync(persistNodes is null ? null : persistNodes[index], cancellationToken);
        }

        for(int index = 0; index < hostCount; index++)
        {
            int host = index;
            NetworkStream serverStream = servers[host].GetStream();
            MessageChannelReader<CorrelatedFrame> serverRequests = new(PipeReader.Create(serverStream), ReadFrame);
            MessageChannelWriter<CorrelatedFrame> serverResponses = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
            ServingTasks[host] = Task.Run(() => ServeAsync(host, serverRequests, serverResponses, cancellationToken), cancellationToken);

            NetworkStream clientStream = clients[host].GetStream();
            RequestWriters[host] = new MessageChannelWriter<CorrelatedFrame>(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
            MessageChannelReader<CorrelatedFrame> clientReplies = new(PipeReader.Create(clientStream), ReadFrame);
            ReplyReaders[host] = clientReplies;
            ReplyEnumerators[host] = clientReplies.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            //One request and its reply at a time per connection: a proposer that abandons a slow recorder
            //mid-step still asks it again at the next step, and an overlapping call would interleave frames
            //on the shared pipe while the abandoned call's reply is still in flight.
            Gates[host] = new SemaphoreSlim(1, 1);
        }
    }


    public QuePaxaLeaderSchedule Schedule { get; }

    /// <summary>
    /// The genesis membership every host of this cluster runs under, which is minted from the agreed order
    /// unless a scenario supplied one, so that a register over the same chain stamps the value the hosts
    /// derive.
    /// </summary>
    /// <remarks>
    /// A supplied genesis need not name every host. A replica this cluster runs a host for while the
    /// membership leaves it out is a joiner before the change that admits it, and a bench whose membership is
    /// its host list cannot state that at all.
    /// </remarks>
    public QuePaxaConfiguration Genesis { get; }

    public int HostCount => Runners.Length;

    /// <summary>The number of requests each host actually served, complete only after <see cref="DrainAsync"/>.</summary>
    public IReadOnlyList<int> Served
    {
        get
        {
            lock(Observations)
            {
                return (int[])ServedCounts.Clone();
            }
        }
    }

    /// <summary>
    /// The number of learn offers each host answered over the wire. The count is settled without a drain for
    /// any offer whose publish was awaited, because each increment happens before that offer's answer leaves
    /// the host.
    /// </summary>
    public IReadOnlyList<int> Disseminated
    {
        get
        {
            lock(Observations)
            {
                return (int[])DisseminatedCounts.Clone();
            }
        }
    }

    /// <summary>
    /// The number of calls each host was asked to answer. It is written on the caller's own flow before the
    /// wire, so unlike the served-side observations it needs no drain.
    /// </summary>
    public IReadOnlyList<int> Sent
    {
        get
        {
            lock(Observations)
            {
                return (int[])SentCounts.Clone();
            }
        }
    }

    /// <summary>The versions a host refused to serve, which is how a test observes the single-live-instance rule.</summary>
    public IReadOnlyList<RegisterVersion> Declined
    {
        get
        {
            lock(Observations)
            {
                return [.. DeclinedVersions];
            }
        }
    }

    /// <summary>
    /// The proposal keys the hosts were asked to record, paired with the version they arrived at, in arrival
    /// order. A key repeats across the steps of one attempt, so a test counting attempts reads the distinct
    /// pairs.
    /// </summary>
    public IReadOnlyList<(RegisterVersion Version, ProposalKey Key)> Recorded
    {
        get
        {
            lock(Observations)
            {
                return [.. RecordedKeys];
            }
        }
    }

    /// <summary>
    /// Which member answered at which version, in arrival order, complete only after
    /// <see cref="DrainAsync"/>.
    /// </summary>
    /// <remarks>
    /// A host that declined the instance is not an answer, so the reading is taken where the host replied
    /// rather than where the request arrived. What it reports is whose answers a quorum could have been
    /// counted over, which is a claim about identities and not only about how many there were.
    /// </remarks>
    public IReadOnlyList<(ReplicaId Member, RegisterVersion Version)> Answered
    {
        get
        {
            lock(Observations)
            {
                return [.. AnsweredVersions];
            }
        }
    }

    /// <summary>
    /// The number of record replies the endpoints decoded, which separates a rejection by the register's
    /// guard from a rejection by the codec.
    /// </summary>
    public int DecodedReplies
    {
        get
        {
            lock(Observations)
            {
                return DecodedReplyCount;
            }
        }
    }


    private Lock Observations { get; } = new();

    private QuePaxaVersionedRunner<TValue>[] Runners { get; }

    private ReceiveCommittedRecordDelegate<TValue>[] Receives { get; }

    private Task[] RunTasks { get; }

    private Task[] ServingTasks { get; }

    private TcpListener[] Listeners { get; }

    private TcpClient[] Clients { get; }

    private TcpClient[] Servers { get; }

    private MessageChannelWriter<CorrelatedFrame>[] RequestWriters { get; }

    private MessageChannelReader<CorrelatedFrame>[] ReplyReaders { get; }

    private IAsyncEnumerator<CorrelatedFrame>[] ReplyEnumerators { get; }

    private SemaphoreSlim[] Gates { get; }

    /// <summary>
    /// A gate this cluster holds, taken by the drain or by teardown and never given back.
    /// </summary>
    /// <remarks>
    /// Teardown waits only on the gates nobody has taken yet, because waiting on one it holds itself would
    /// never return.
    /// </remarks>
    private bool[] GateTaken { get; }

    private int[] NextIds { get; }

    private bool[] Partitioned { get; }

    private int[] ServedCounts { get; }

    private int[] DisseminatedCounts { get; }

    private int[] SentCounts { get; }

    private int DecodedReplyCount { get; set; }

    private List<RegisterVersion> DeclinedVersions { get; } = [];

    private List<(ReplicaId Member, RegisterVersion Version)> AnsweredVersions { get; } = [];

    private List<(RegisterVersion Version, ProposalKey Key)> RecordedKeys { get; } = [];

    private TamperReplyPayloadDelegate? TamperReplyPayload { get; }

    private WriteValueDelegate<Utf8JsonWriter, VersionedValue<TValue>> WriteRecordValue { get; }

    private ReadValueDelegate<JsonElement, VersionedValue<TValue>> ReadRecordValue { get; }

    private SerializeMessageDelegate<VersionedRecordRequest<VersionedValue<TValue>>> RequestSerialize { get; }

    private DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<TValue>>> RequestDeserialize { get; }

    private SerializeMessageDelegate<VersionedRecordReply<VersionedValue<TValue>>> ReplySerialize { get; }

    private DeserializeMessageDelegate<VersionedRecordReply<VersionedValue<TValue>>> ReplyDeserialize { get; }

    private bool Drained { get; set; }


    /// <summary>
    /// Starts <paramref name="hostCount"/> runner-backed hosts, each behind its own loopback connection.
    /// </summary>
    /// <param name="schedule">The leader schedule every host is configured with.</param>
    /// <param name="hostCount">The number of hosts.</param>
    /// <param name="writeValue">Writes an application value to the JSON writer.</param>
    /// <param name="readValue">Reads an application value from a JSON element.</param>
    /// <param name="cancellationToken">The token the runner loops, the serve loops and the connect run under.</param>
    /// <param name="committed">The record every host starts having learned, or <see langword="null"/> for a fresh cluster.</param>
    /// <param name="tamperReplyPayload">An optional rewrite of the reply bytes a record endpoint receives.</param>
    /// <param name="genesis">The chain's genesis membership, which need not name every host; minted from the agreed order when <see langword="null"/>.</param>
    /// <param name="persistNodes">One store per host, or <see langword="null"/> for hosts whose loops run without one.</param>
    /// <returns>A connected cluster.</returns>
    /// <remarks>
    /// <para>
    /// This is a factory rather than a constructor because the connections must be established before the
    /// instance exists, and a socket cannot be connected without awaiting.
    /// </para>
    /// <para>
    /// The stores are per scenario and never blanket. A store makes every reply a host hands back wait on a
    /// write, so a cluster given one is a cluster whose per-request work differs from every other scenario's,
    /// and only a scenario that reads what was written is entitled to that cost.
    /// </para>
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listeners and clients are handed to the instance, which disposes them in DisposeAsync, and a failure before that instance exists disposes everything started here.")]
    public static async Task<SocketVersionedQuePaxaCluster<TValue>> ConnectAsync(
        QuePaxaLeaderSchedule schedule,
        int hostCount,
        WriteValueDelegate<Utf8JsonWriter, TValue> writeValue,
        ReadValueDelegate<JsonElement, TValue> readValue,
        CancellationToken cancellationToken,
        VersionedValue<TValue>? committed = null,
        TamperReplyPayloadDelegate? tamperReplyPayload = null,
        QuePaxaConfiguration? genesis = null,
        PersistVersionedNodeDelegate<TValue>[]? persistNodes = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostCount, 1);
        ArgumentNullException.ThrowIfNull(writeValue);
        ArgumentNullException.ThrowIfNull(readValue);
        if(persistNodes is not null && persistNodes.Length != hostCount)
        {
            throw new ArgumentException("A cluster takes one store per host, so a shorter list would leave a host's durability decided by its position.", nameof(persistNodes));
        }

        List<TcpListener> listeners = new(hostCount);
        List<TcpClient> clients = new(hostCount);
        Task<TcpClient>[] acceptTasks = [];
        try
        {
            var ports = new int[hostCount];
            for(int index = 0; index < hostCount; index++)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listeners.Add(listener);
                listener.Start();
                ports[index] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            acceptTasks = [.. listeners.Select(listener => listener.AcceptTcpClientAsync(cancellationToken).AsTask())];
            for(int index = 0; index < hostCount; index++)
            {
                TcpClient client = new();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, ports[index], cancellationToken).ConfigureAwait(false);
            }

            TcpClient[] servers = await Task.WhenAll(acceptTasks).ConfigureAwait(false);

            return new SocketVersionedQuePaxaCluster<TValue>(schedule, genesis, writeValue, readValue, committed, tamperReplyPayload, persistNodes, [.. listeners], [.. clients], servers, cancellationToken);
        }
        catch(Exception)
        {
            //A half-built cluster has no owner to dispose it, so every listener started, every client
            //created and every accept that did complete is closed here before the failure travels on.
            foreach(TcpListener listener in listeners)
            {
                listener.Dispose();
            }

            foreach(TcpClient client in clients)
            {
                client.Dispose();
            }

            foreach(Task<TcpClient> accept in acceptTasks)
            {
                if(accept.IsCompletedSuccessfully)
                {
                    (await accept.ConfigureAwait(false)).Dispose();
                }
            }

            throw;
        }
    }


    /// <summary>Cuts one host off, so every later call to it fails before it reaches the wire.</summary>
    /// <param name="index">The host.</param>
    /// <remarks>
    /// The host keeps running behind the cut and keeps everything it had learned, so what a cut takes away is
    /// the route and never the host: a healed host answers again, with the record it held while nothing could
    /// ask it for one.
    /// </remarks>
    public void Partition(int index) => Partitioned[index] = true;


    /// <summary>Restores one host.</summary>
    /// <param name="index">The host.</param>
    public void Heal(int index) => Partitioned[index] = false;


    /// <summary>Tells one host about a committed record, which is the dissemination a deployment owes.</summary>
    /// <param name="index">The host.</param>
    /// <param name="committed">A decided record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the record advanced that host.</returns>
    /// <remarks>
    /// This is the push a register's <see cref="PublishCommittedRecordDelegate{TValue}"/> is wired to here,
    /// so the learn names <see cref="LearnDurability.Durable"/>: a sender states its own durability
    /// obligation rather than assuming what the receiver does with the record, and the record that installs
    /// a membership may be the only copy of it inside the membership it installs. The runners of this
    /// cluster run without a store, so the naming is the contract and costs no write here.
    /// </remarks>
    public async Task<bool> LearnAtAsync(int index, VersionedValue<TValue> committed, CancellationToken cancellationToken)
    {
        return await Runners[index].LearnAsync(committed, LearnDurability.Durable, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Tells one host about a committed record, requiring nothing of that host's store.</summary>
    /// <param name="index">The host.</param>
    /// <param name="committed">A decided record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the record advanced that host.</returns>
    /// <remarks>
    /// This is the other half of the durability contract <see cref="LearnAtAsync"/> names, kept as a separate
    /// seam so a scenario states which obligation the sender took on. A record that installs a membership is
    /// made durable under either naming, which is what leaves the ordinary record as the one shape the two
    /// namings can be told apart on.
    /// </remarks>
    public async Task<bool> LearnInMemoryAtAsync(int index, VersionedValue<TValue> committed, CancellationToken cancellationToken)
    {
        return await Runners[index].LearnAsync(committed, LearnDurability.InMemory, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Tells every host about a committed record.</summary>
    /// <param name="committed">A decided record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once every host has adopted or refused the record.</returns>
    public async Task LearnAllAsync(VersionedValue<TValue> committed, CancellationToken cancellationToken)
    {
        for(int index = 0; index < Runners.Length; index++)
        {
            _ = await LearnAtAsync(index, committed, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Offers a committed record to its audience over each member's own connection, which is this cluster's
    /// wire-crossing <see cref="PublishCommittedRecordDelegate{TValue}"/> by method-group conversion.
    /// </summary>
    /// <param name="committed">The decided record.</param>
    /// <param name="audience">The hosts to offer it to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once every member of the audience has answered or failed.</returns>
    /// <remarks>
    /// The receiving end is a <see cref="ReceiveCommittedRecordDelegate{TValue}"/> served behind the same
    /// connection the record exchanges use, and this sender names <see cref="LearnDurability.Durable"/> for
    /// every offer, as <see cref="LearnAtAsync"/> does. A member the offer cannot reach is skipped rather
    /// than failing the rest, because an unreachable host is that host's unavailability and the publish
    /// contract owes the caller nothing for it. <see cref="LearnAllAsync"/> stays the in-process push a
    /// scenario uses when the wire is not its subject.
    /// </remarks>
    public async ValueTask PublishAsync(VersionedValue<TValue> committed, ImmutableArray<ReplicaId> audience, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using(var writer = new Utf8JsonWriter(buffer))
        {
            WriteRecordValue(writer, committed);
        }

        byte[] payload = buffer.WrittenSpan.ToArray();
        foreach(ReplicaId member in audience)
        {
            try
            {
                _ = await ExchangeAsync(IndexOf(member), LearnKind, payload, cancellationToken).ConfigureAwait(false);
            }
            catch(IOException)
            {
                //A member the offer cannot reach is that host's unavailability, and the next member is still
                //owed its offer.
            }
        }
    }


    /// <summary>
    /// Resolves the endpoint of one member, which is this cluster's
    /// <see cref="ResolveRecorderEndpointDelegate{TValue}"/>.
    /// </summary>
    /// <param name="member">The member to reach.</param>
    /// <returns>That member's endpoint, over the same connection every other call to it uses.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member, which is how the resolver reports one it cannot resolve.</exception>
    public VersionedRecorderEndpointDelegate<VersionedValue<TValue>> Resolve(ReplicaId member) => Endpoints()[IndexOf(member)];


    /// <summary>
    /// Resolves the catch-up reader of one member, which is this cluster's
    /// <see cref="ResolveCommittedRecordReaderDelegate{TValue}"/>.
    /// </summary>
    /// <param name="member">The member to ask.</param>
    /// <returns>That member's reader.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member.</exception>
    public ReadCommittedRecordDelegate<TValue> ResolveReader(ReplicaId member) => Readers()[IndexOf(member)];


    /// <summary>
    /// Reports which version the host that is <paramref name="member"/> holds, asked over that member's own
    /// connection through a dedicated probe frame, which is this cluster's
    /// <see cref="ObserveMemberVersionDelegate"/>.
    /// </summary>
    /// <param name="member">The member to ask.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>That member's answer, carrying the identity the answering serve loop asserts for itself
    /// beside the version, which is <see cref="RegisterVersion.Unwritten"/> when it holds no record.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member.</exception>
    /// <exception cref="IOException">Thrown when the call to that member faults, which is what a host behind a cut route or a drained cluster answers with.</exception>
    /// <remarks>
    /// The question crosses the wire rather than reading the state behind it, which is what lets a report
    /// separate a member that has learned nothing from one nothing reaches: the first answers unwritten over a
    /// working connection and the second answers not at all. The identity travels in the reply's own field,
    /// asserted by the serve loop that answered rather than copied from the member this side aimed at, so the
    /// register's mis-wiring refusal compares against a genuine claim.
    /// </remarks>
    public async ValueTask<MemberVersionReport> ObserveMemberVersionAsync(ReplicaId member, CancellationToken cancellationToken)
    {
        byte[] payload = await ExchangeAsync(IndexOf(member), VersionKind, JsonNull, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload);
        ReplicaId recorder = ReplicaId.FromSpan(Convert.FromHexString(document.RootElement.GetProperty("recorder").GetString()!));
        var version = new RegisterVersion(document.RootElement.GetProperty("version").GetUInt64());

        return new MemberVersionReport(recorder, version);
    }


    /// <summary>
    /// The record endpoints, one per host, over this cluster's shared per-host connections.
    /// </summary>
    /// <returns>One endpoint per host.</returns>
    /// <remarks>
    /// Every call returns endpoints over the same connections, gates and correlation counters, so two
    /// registers built from two calls contend for one wire per host exactly as two writers in a deployment do.
    /// </remarks>
    public VersionedRecorderEndpointDelegate<VersionedValue<TValue>>[] Endpoints()
    {
        var endpoints = new VersionedRecorderEndpointDelegate<VersionedValue<TValue>>[HostCount];
        for(int index = 0; index < endpoints.Length; index++)
        {
            int host = index;
            endpoints[index] = async (request, token) =>
            {
                var buffer = new ArrayBufferWriter<byte>();
                RequestSerialize(request, buffer);
                byte[] payload = await ExchangeAsync(host, RecordKind, buffer.WrittenSpan.ToArray(), token).ConfigureAwait(false);
                byte[] delivered = TamperReplyPayload is null ? payload : TamperReplyPayload(host, payload);
                VersionedRecordReply<VersionedValue<TValue>> reply = ReplyDeserialize(new ReadOnlySequence<byte>(delivered));

                //The count is taken only once the codec has accepted the bytes, so bytes the codec refused
                //are not counted as a reply the register was given.
                lock(Observations)
                {
                    DecodedReplyCount++;
                }

                return reply;
            };
        }

        return endpoints;
    }


    /// <summary>
    /// The catch-up readers, one per host, over the same connections the endpoints use.
    /// </summary>
    /// <returns>One reader per host.</returns>
    public ReadCommittedRecordDelegate<TValue>[] Readers()
    {
        var readers = new ReadCommittedRecordDelegate<TValue>[HostCount];
        for(int index = 0; index < readers.Length; index++)
        {
            int host = index;
            readers[index] = async token =>
            {
                byte[] payload = await ExchangeAsync(host, ReadKind, JsonNull, token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(payload);
                if(document.RootElement.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                return ReadRecordValue(document.RootElement);
            };
        }

        return readers;
    }


    /// <summary>The index of the host that is <paramref name="member"/>.</summary>
    /// <param name="member">The member to look for.</param>
    /// <returns>That host's index.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member.</exception>
    private int IndexOf(ReplicaId member)
    {
        for(int index = 0; index < HostCount; index++)
        {
            if(Schedule.Schedule.Order[index].Equals(member))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"No host of this cluster is {member}.");
    }


    /// <summary>
    /// Ends every connection's request side and waits for the serve loops to finish what is in flight.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once no serve loop is running.</returns>
    /// <remarks>
    /// <para>
    /// This is the barrier the observations need, and it is idempotent so a test that drains explicitly and
    /// then disposes does not shut a closed socket down twice.
    /// </para>
    /// <para>
    /// The client side is quiesced before the send side closes. A proposer that abandoned a call left it
    /// queued on its host's gate with its request not yet on the wire, and cutting the connection there
    /// would drop a request the in-memory bench records, so the drain takes every gate first and never
    /// gives one back: after a drain the cluster serves nothing further.
    /// </para>
    /// </remarks>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        if(Drained)
        {
            return;
        }

        Drained = true;
        for(int index = 0; index < Gates.Length; index++)
        {
            if(!await Gates[index].WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException($"Host {index}'s gate still carried a call when the cluster drained, so the observations are incomplete.");
            }

            GateTaken[index] = true;
        }

        foreach(TcpClient client in Clients)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch(ObjectDisposedException)
            {
                //A socket already torn down needs no shutdown.
            }
            catch(SocketException)
            {
                //A peer that closed first leaves nothing to shut down either.
            }
        }

        await Task.WhenAll(ServingTasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Drains the cluster and then tears its transport and its runners down.</summary>
    /// <returns>A task that completes once nothing this cluster started is still running.</returns>
    /// <remarks>
    /// Teardown reports nothing of its own, because a failure here would mask the test's own failure, and
    /// every stage is guarded on its own so a stage that fails still leaves the later ones to run.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch(Exception)
        {
            //A drain that timed out or faulted still leaves a transport to close.
        }

        //Disposing an enumerator whose call is still in flight is illegal, and a drain that timed out is exactly
        //the path that leaves one parked. A cancel only arms that call's completion, which lands on the pipe's
        //own IO continuation while this method runs on, so the gate take below is the barrier that proves the
        //parked call finished: an endpoint releases its gate only once its MoveNextAsync has completed.
        //Cancelling is unconditional because it is tolerated on a stream that already ended.
        foreach(MessageChannelReader<CorrelatedFrame> reader in ReplyReaders)
        {
            reader.CancelPendingRead();
        }

        //Teardown holds what it takes, as the drain does, so only the gates the drain never acquired are waited
        //for here. A wait that expires leaves a call still parked, which the swallowing catch around disposal
        //absorbs, because teardown must not throw over a test's own outcome.
        for(int index = 0; index < Gates.Length; index++)
        {
            if(GateTaken[index])
            {
                continue;
            }

            try
            {
                GateTaken[index] = await Gates[index].WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch(Exception)
            {
                //A gate the runtime has already reclaimed carries no call to wait for either.
            }
        }

        foreach(IAsyncEnumerator<CorrelatedFrame> enumerator in ReplyEnumerators)
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch(Exception)
            {
                //One reader that faults as it completes must not cost the others their disposal.
            }
        }

        try
        {
            foreach(SemaphoreSlim gate in Gates)
            {
                gate.Dispose();
            }

            foreach(TcpClient client in Clients)
            {
                client.Dispose();
            }

            foreach(TcpClient server in Servers)
            {
                server.Dispose();
            }

            foreach(TcpListener listener in Listeners)
            {
                listener.Dispose();
            }
        }
        catch(Exception)
        {
            //A handle the runtime has already reclaimed is the only thing this stage can fail on.
        }

        try
        {
            foreach(QuePaxaVersionedRunner<TValue> runner in Runners)
            {
                runner.Complete();
            }

            await Task.WhenAll(RunTasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch(Exception)
        {
            //A runner loop ended by its token faults its task, and the bound covers a loop that did not end.
        }
    }


    private async Task<byte[]> ExchangeAsync(int host, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        //A drain takes every gate permanently, so a later call would wait forever on a gate nobody returns.
        if(Drained)
        {
            throw new IOException($"Host {host} is behind a drained cluster, which serves nothing further.");
        }

        //A partitioned host never sees the request, so it records nothing: the check precedes the gate and
        //the wire alike.
        if(Partitioned[host])
        {
            throw new IOException($"Host {host} is partitioned.");
        }

        //This runs on the caller's own flow before any await, so the count is settled by the time the call's
        //task is handed back and needs no drain to be read.
        lock(Observations)
        {
            SentCounts[host]++;
        }

        await Gates[host].WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int id = ++NextIds[host];
            await RequestWriters[host].WriteAsync(new CorrelatedFrame(id, kind, payload), cancellationToken).ConfigureAwait(false);
            if(!await ReplyEnumerators[host].MoveNextAsync().ConfigureAwait(false))
            {
                throw new IOException($"Host {host} closed its reply stream while call {id} was outstanding.");
            }

            CorrelatedFrame answer = ReplyEnumerators[host].Current;
            if(answer.Id != id)
            {
                throw new InvalidOperationException($"Host {host} answered call {answer.Id} while call {id} was the outstanding one.");
            }

            if(answer.Payload is null)
            {
                throw new IOException($"Call {id} faulted at recorder host {host}.");
            }

            return answer.Payload;
        }
        finally
        {
            _ = Gates[host].Release();
        }
    }


    private async Task ServeAsync(int host, MessageChannelReader<CorrelatedFrame> requests, MessageChannelWriter<CorrelatedFrame> responses, CancellationToken cancellationToken)
    {
        await foreach(CorrelatedFrame frame in requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            CorrelatedFrame response;
            try
            {
                response = frame.Kind switch
                {
                    ReadKind => await ServeReadAsync(host, frame, cancellationToken).ConfigureAwait(false),
                    LearnKind => await ServeLearnAsync(host, frame, cancellationToken).ConfigureAwait(false),
                    VersionKind => await ServeVersionAsync(host, frame, cancellationToken).ConfigureAwait(false),
                    _ => await ServeRecordAsync(host, frame, cancellationToken).ConfigureAwait(false),
                };
            }
            catch(Exception)
            {
                //A host fault reaches the caller as a fault frame carrying the correlation and nothing else,
                //because no protocol field can carry the reason.
                response = new CorrelatedFrame(frame.Id, frame.Kind, null);
            }

            //The fault frame exists to carry the correlation out when serving failed, and the failure may be
            //the token itself, so its write must not ride the token that caused it.
            CancellationToken writeToken = response.Payload is null ? CancellationToken.None : cancellationToken;
            await responses.WriteAsync(response, writeToken).ConfigureAwait(false);
        }
    }


    private async Task<CorrelatedFrame> ServeRecordAsync(int host, CorrelatedFrame frame, CancellationToken cancellationToken)
    {
        VersionedRecordRequest<VersionedValue<TValue>> request = RequestDeserialize(new ReadOnlySequence<byte>(frame.Payload!));

        //The arrival is recorded before the host is asked, so a request the host refuses is still one it saw.
        lock(Observations)
        {
            RecordedKeys.Add((request.Version, request.Request.Proposal.Key));
        }

        VersionedRecordReply<VersionedValue<TValue>> reply;
        try
        {
            reply = await Runners[host].RecordAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch(ArgumentOutOfRangeException)
        {
            //This is the refusal a versioned host raises for a request that does not name its live version.
            lock(Observations)
            {
                DeclinedVersions.Add(request.Version);
            }

            return new CorrelatedFrame(frame.Id, RecordKind, null);
        }

        lock(Observations)
        {
            ServedCounts[host]++;

            //Recorded where the host answered rather than where the request arrived, so a host that declined
            //the instance is one this reading does not count as an answer.
            AnsweredVersions.Add((Schedule.Schedule.Order[host], reply.Version));
        }

        var buffer = new ArrayBufferWriter<byte>();
        ReplySerialize(reply, buffer);

        return new CorrelatedFrame(frame.Id, RecordKind, buffer.WrittenSpan.ToArray());
    }


    private async Task<CorrelatedFrame> ServeReadAsync(int host, CorrelatedFrame frame, CancellationToken cancellationToken)
    {
        VersionedValue<TValue>? record = await Runners[host].ReadCommittedAsync(cancellationToken).ConfigureAwait(false);
        if(record is null)
        {
            return new CorrelatedFrame(frame.Id, ReadKind, JsonNull);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using(var writer = new Utf8JsonWriter(buffer))
        {
            WriteRecordValue(writer, record);
        }

        return new CorrelatedFrame(frame.Id, ReadKind, buffer.WrittenSpan.ToArray());
    }


    private async Task<CorrelatedFrame> ServeLearnAsync(int host, CorrelatedFrame frame, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(frame.Payload!);
        VersionedValue<TValue> committed = ReadRecordValue(document.RootElement);

        //The arrival is counted before the host is offered the record and the count leaves with the answer,
        //so a publisher that awaited its offers reads a settled count without a drain.
        lock(Observations)
        {
            DisseminatedCounts[host]++;
        }

        bool adopted = await Receives[host](committed, LearnDurability.Durable, cancellationToken).ConfigureAwait(false);

        return new CorrelatedFrame(frame.Id, LearnKind, adopted ? JsonTrue : JsonFalse);
    }


    private async Task<CorrelatedFrame> ServeVersionAsync(int host, CorrelatedFrame frame, CancellationToken cancellationToken)
    {
        VersionedValue<TValue>? record = await Runners[host].ReadCommittedAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new ArrayBufferWriter<byte>();
        using(var writer = new Utf8JsonWriter(buffer))
        {
            //The identity is the serve loop's own, never echoed off the request, so the probe's reply carries
            //a genuine claim the register's mis-wiring refusal can compare against.
            writer.WriteStartObject();
            writer.WriteString("recorder", Convert.ToHexStringLower(Schedule.Schedule.Order[host].AsSpan()));
            writer.WriteNumber("version", record is null ? RegisterVersion.Unwritten.Value : record.Version.Value);
            writer.WriteEndObject();
        }

        return new CorrelatedFrame(frame.Id, VersionKind, buffer.WrittenSpan.ToArray());
    }


    private static void WriteFrame(CorrelatedFrame frame, IBufferWriter<byte> destination)
    {
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("id", frame.Id);
        writer.WriteNumber("kind", frame.Kind);
        if(frame.Payload is null)
        {
            writer.WriteBoolean("fault", true);
        }
        else
        {
            writer.WritePropertyName("payload");
            writer.WriteRawValue(frame.Payload);
        }

        writer.WriteEndObject();
    }


    private static CorrelatedFrame ReadFrame(ReadOnlySequence<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        int id = document.RootElement.GetProperty("id").GetInt32();
        byte kind = document.RootElement.GetProperty("kind").GetByte();
        if(document.RootElement.TryGetProperty("payload", out JsonElement inner))
        {
            return new CorrelatedFrame(id, kind, Encoding.UTF8.GetBytes(inner.GetRawText()));
        }

        return new CorrelatedFrame(id, kind, null);
    }


    /// <summary>
    /// One framed call or answer. The kind tells a record exchange from a catch-up read, a learn offer and a
    /// version probe, and an absent
    /// payload is the opaque fault an answer carries when the host could not serve the call.
    /// </summary>
    private sealed record CorrelatedFrame(int Id, byte Kind, byte[]? Payload);
}
