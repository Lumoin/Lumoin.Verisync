using Lumoin.Base;
using Lumoin.Verisync.Cbor;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Formats.Cbor;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

int passed = 0;
int failed = 0;

/// <summary>Counts and prints an assertion so the console and process exit code expose failures.</summary>
/// <param name="name">The behavior being checked.</param>
/// <param name="hasPassed">Whether the behavior meets the expected result.</param>
void Check(string name, bool hasPassed)
{
    if(hasPassed)
    {
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"FAIL {name}");
    }
}

try
{
    using BaseMemoryPool pool = new();
    using(IMemoryOwner<byte> rental = pool.Rent(64))
    {
        bool managed = MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)rental.Memory, out _);
        Console.WriteLine($"INFO BaseMemoryPool backing: {(managed ? "managed array" : "non-array memory")}");
        rental.Memory.Span.Fill(0x5A);
        Check("BaseMemoryPool rents an exact writable managed buffer", managed && rental.Memory.Length == 64
            && rental.Memory.Span.IndexOfAnyExcept((byte)0x5A) < 0);
    }
    using(IMemoryOwner<byte> returned = pool.Rent(64))
    {
        Check("BaseMemoryPool returns a cleared buffer", returned.Memory.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    ReplicaId r1 = Replica(1);
    ReplicaId r2 = Replica(2);

    GCounter g1 = GCounter.Empty.Increment(r1, 3);
    GCounter g2 = GCounter.Empty.Increment(r2, 2);
    GCounter gMerged = g1.Merge(g2);
    GCounter gReloaded1 = GCounter.FromState(RoundTrip(g1.ToState(),
        CrdtStateJson.CreateGCounterStateSerializer(), CrdtStateJson.CreateGCounterStateDeserializer()));
    GCounter gReloaded2 = GCounter.FromState(RoundTrip(g2.ToState(),
        CrdtStateJson.CreateGCounterStateSerializer(), CrdtStateJson.CreateGCounterStateDeserializer()));
    Check("GCounter JSON states preserve a convergent two-replica merge", gMerged.Value == 5
        && gMerged.Equals(gReloaded1.Merge(gReloaded2)) && gMerged.Equals(gReloaded2.Merge(gReloaded1)));

    PNCounter pn1 = PNCounter.Empty.Increment(r1, 7).Decrement(r1, 2);
    PNCounter pn2 = PNCounter.Empty.Increment(r2, 3).Decrement(r2, 4);
    PNCounter pnMerged = pn1.Merge(pn2);
    PNCounter pnReloaded1 = PNCounter.FromState(RoundTrip(pn1.ToState(),
        CrdtStateJson.CreatePNCounterStateSerializer(), CrdtStateJson.CreatePNCounterStateDeserializer()));
    PNCounter pnReloaded2 = PNCounter.FromState(RoundTrip(pn2.ToState(),
        CrdtStateJson.CreatePNCounterStateSerializer(), CrdtStateJson.CreatePNCounterStateDeserializer()));
    Check("PNCounter JSON states preserve a convergent two-replica merge", pnMerged.Value == 4
        && pnMerged.Equals(pnReloaded1.Merge(pnReloaded2)) && pnMerged.Equals(pnReloaded2.Merge(pnReloaded1)));

    OrSet<string> ancestor = OrSet<string>.Empty.Add("shared", r1);
    OrSet<string> removed = ancestor.Remove("shared");
    OrSet<string> added = ancestor.Add("shared", r2).Add("second", r2);
    OrSet<string> orMerged = removed.Merge(added);
    OrSet<string> orReloaded1 = OrSet<string>.FromState(RoundTrip(removed.ToState(),
        CrdtStateJson.CreateOrSetStateSerializer<string>(WriteString), CrdtStateJson.CreateOrSetStateDeserializer(ReadString)));
    OrSet<string> orReloaded2 = OrSet<string>.FromState(RoundTrip(added.ToState(),
        CrdtStateJson.CreateOrSetStateSerializer<string>(WriteString), CrdtStateJson.CreateOrSetStateDeserializer(ReadString)));
    Check("OrSet JSON states preserve concurrent add over observed remove", !orReloaded1.Contains("shared")
        && orMerged.Contains("shared") && orMerged.Contains("second")
        && orMerged.Equals(orReloaded1.Merge(orReloaded2)) && orMerged.Equals(orReloaded2.Merge(orReloaded1)));

    LwwRegister<string> lww1 = LwwRegister<string>.Empty.Write("first", new Timestamp(10), r1);
    LwwRegister<string> lww2 = LwwRegister<string>.Empty.Write("second", new Timestamp(20), r2);
    LwwRegister<string> lwwMerged = lww1.Merge(lww2);
    LwwRegister<string> lwwReloaded1 = LwwRegister<string>.FromState(RoundTrip(lww1.ToState(),
        CrdtStateJson.CreateLwwRegisterStateSerializer<string>(WriteString), CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
    LwwRegister<string> lwwReloaded2 = LwwRegister<string>.FromState(RoundTrip(lww2.ToState(),
        CrdtStateJson.CreateLwwRegisterStateSerializer<string>(WriteString), CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
    Check("LwwRegister JSON states preserve the later write on merge", lwwMerged.Value == "second"
        && lwwMerged.Equals(lwwReloaded1.Merge(lwwReloaded2)) && lwwMerged.Equals(lwwReloaded2.Merge(lwwReloaded1)));

    MvRegister<string> mv1 = MvRegister<string>.Empty.Write("first", r1);
    MvRegister<string> mv2 = MvRegister<string>.Empty.Write("second", r2);
    MvRegister<string> mvMerged = mv1.Merge(mv2);
    MvRegister<string> mvReloaded1 = MvRegister<string>.FromState(RoundTrip(mv1.ToState(),
        CrdtStateJson.CreateMvRegisterStateSerializer<string>(WriteString), CrdtStateJson.CreateMvRegisterStateDeserializer(ReadString)));
    MvRegister<string> mvReloaded2 = MvRegister<string>.FromState(RoundTrip(mv2.ToState(),
        CrdtStateJson.CreateMvRegisterStateSerializer<string>(WriteString), CrdtStateJson.CreateMvRegisterStateDeserializer(ReadString)));
    Check("MvRegister JSON states retain both concurrent writes on merge", mvMerged.Values.Count == 2
        && mvMerged.Values.Contains("first") && mvMerged.Values.Contains("second")
        && mvMerged.Equals(mvReloaded1.Merge(mvReloaded2)) && mvMerged.Equals(mvReloaded2.Merge(mvReloaded1)));

    (Rga<string> rgaAncestor, Dot head) = Rga<string>.Empty.InsertAtHead("head", r1);
    (Rga<string> rga1, _) = rgaAncestor.InsertAfter(head, "left", r1);
    (Rga<string> rga2, _) = rgaAncestor.InsertAfter(head, "right", r2);
    Rga<string> rgaMerged = rga1.Merge(rga2);
    Rga<string> rgaReloaded1 = Rga<string>.FromState(RoundTrip(rga1.ToState(),
        CrdtStateJson.CreateRgaStateSerializer<string>(WriteString), CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
    Rga<string> rgaReloaded2 = Rga<string>.FromState(RoundTrip(rga2.ToState(),
        CrdtStateJson.CreateRgaStateSerializer<string>(WriteString), CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
    Check("Rga JSON states preserve convergent concurrent inserts", rgaMerged.Count == 3
        && rgaMerged.Values.Contains("left") && rgaMerged.Values.Contains("right")
        && rgaMerged.Equals(rgaReloaded1.Merge(rgaReloaded2)) && rgaMerged.Equals(rgaReloaded2.Merge(rgaReloaded1)));

    SmokeMessage message = new(7, "wasm channel message");
    Check("CBOR channel message round-trips over an in-memory pipe", message == await RoundTripChannelAsync(message,
        CborChannelSerialization.CreateSerializer<SmokeMessage>(WriteMessage), CborChannelSerialization.CreateDeserializer(ReadMessage)).ConfigureAwait(false));
    Check("JSON channel message round-trips over an in-memory pipe", message == await RoundTripChannelAsync(message,
        JsonChannelSerialization.CreateSerializer(SmokeJsonContext.Default.SmokeMessage),
        JsonChannelSerialization.CreateDeserializer(SmokeJsonContext.Default.SmokeMessage)).ConfigureAwait(false));

    Check("Anti-entropy incremental convergence", await ReconcileAsync(useSnapshot: false).ConfigureAwait(false));
    Check("Reconciliation snapshot stream agreement", SnapshotStreamAgrees());
    Check("Anti-entropy snapshot convergence", await ReconcileAsync(useSnapshot: true).ConfigureAwait(false));

    Console.WriteLine($"INFO ReconciliationXorVector128Backend.IsSupported: {ReconciliationXorVector128Backend.IsSupported}");
    Console.WriteLine($"INFO ReconciliationXorVector256Backend.IsSupported: {ReconciliationXorVector256Backend.IsSupported}");
    Console.WriteLine($"INFO ReconciliationXorVector512Backend.IsSupported: {ReconciliationXorVector512Backend.IsSupported}");
    Check("XOR vector and scalar agreement", XorAgrees());
    await SmokeConsensusAsync(Check).ConfigureAwait(false);
}
catch(Exception exception)
{
    failed++;
    Console.WriteLine($"FAIL unhandled exception: {exception}");
}

Console.WriteLine(failed == 0
    ? $"WASM-SMOKE-SUCCESS: {passed} passed"
    : $"WASM-SMOKE-FAILURE: {passed} passed, {failed} failed");

return failed;

/// <summary>Creates a deterministic replica identity with all unused bytes cleared.</summary>
/// <param name="id">The distinguishing first byte.</param>
/// <returns>The fixed-width replica identity.</returns>
static ReplicaId Replica(byte id)
{
    byte[] bytes = new byte[ReplicaId.Size];
    bytes[0] = id;

    return ReplicaId.FromSpan(bytes);
}

/// <summary>Persists state as actual codec bytes and reloads it for a later merge.</summary>
/// <typeparam name="TState">The persisted state type.</typeparam>
/// <param name="state">The state to encode.</param>
/// <param name="serialize">The JSON state serializer.</param>
/// <param name="deserialize">The JSON state deserializer.</param>
/// <returns>The decoded state.</returns>
static TState RoundTrip<TState>(TState state, SerializeMessageDelegate<TState> serialize, DeserializeMessageDelegate<TState> deserialize)
{
    ArrayBufferWriter<byte> buffer = new();
    serialize(state, buffer);

    return deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));
}

/// <summary>Writes a string value without reflection-based serialization.</summary>
/// <param name="writer">The JSON destination.</param>
/// <param name="value">The string value.</param>
static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);

/// <summary>Reads a required string value from a CRDT state.</summary>
/// <param name="element">The JSON value.</param>
/// <returns>The non-null string.</returns>
static string ReadString(JsonElement element) => element.GetString() ?? throw new JsonException("A string is required.");

/// <summary>Writes the message as a fixed-size CBOR array.</summary>
/// <param name="writer">The CBOR destination.</param>
/// <param name="message">The message to encode.</param>
static void WriteMessage(CborWriter writer, SmokeMessage message)
{
    writer.WriteStartArray(2);
    writer.WriteInt32(message.Sequence);
    writer.WriteTextString(message.Payload);
    writer.WriteEndArray();
}

/// <summary>Reads the fixed-size CBOR message array.</summary>
/// <param name="reader">The CBOR source.</param>
/// <returns>The decoded message.</returns>
static SmokeMessage ReadMessage(CborReader reader)
{
    reader.ReadStartArray();
    int sequence = reader.ReadInt32();
    string payload = reader.ReadTextString();
    reader.ReadEndArray();

    return new SmokeMessage(sequence, payload);
}

/// <summary>Exercises message framing and a codec on a pipe without concurrent workers.</summary>
/// <param name="message">The message to send.</param>
/// <param name="serialize">The channel serializer.</param>
/// <param name="deserialize">The channel deserializer.</param>
/// <returns>The only received message, or null if the channel has a different message count.</returns>
static async Task<SmokeMessage?> RoundTripChannelAsync(SmokeMessage message,
    SerializeMessageDelegate<SmokeMessage> serialize, DeserializeMessageDelegate<SmokeMessage> deserialize)
{
    Pipe pipe = new();
    MessageChannelWriter<SmokeMessage> writer = new(pipe.Writer, serialize);
    MessageChannelReader<SmokeMessage> reader = new(pipe.Reader, deserialize);
    await writer.WriteAsync(message).ConfigureAwait(false);
    await writer.CompleteAsync().ConfigureAwait(false);
    List<SmokeMessage> received = [];
    await foreach(SmokeMessage value in reader.ReadAllAsync().ConfigureAwait(false))
    {
        received.Add(value);
    }

    return received.Count == 1 ? received[0] : null;
}

/// <summary>Reconciles divergent replicas using exact symbol counts and in-memory session channels.</summary>
static async Task<bool> ReconcileAsync(bool useSnapshot)
{
    ReconciliationContract contract = ReconciliationContract.ContentHashDefault;
    ReplicaId ancestorReplica = Replica(1);
    ReplicaId initiatorReplica = Replica(2);
    ReplicaId responderReplica = Replica(3);
    OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", ancestorReplica).Add("beta", ancestorReplica).Add("gamma", ancestorReplica);
    OrSet<string> initiatorResult = ancestor.Add("delta", initiatorReplica).Add("epsilon", initiatorReplica);
    OrSet<string> responderResult = ancestor.Add("zeta", responderReplica);
    Dictionary<string, string> initiatorDirectory = ReconciliationDirectory(initiatorResult);
    Dictionary<string, string> responderDirectory = ReconciliationDirectory(responderResult);
    ReadOnlyMemory<byte>[] initiatorItems = [.. initiatorDirectory.Keys.Select(hex => (ReadOnlyMemory<byte>)Convert.FromHexString(hex))];
    ReadOnlyMemory<byte>[] responderItems = [.. responderDirectory.Keys.Select(hex => (ReadOnlyMemory<byte>)Convert.FromHexString(hex))];
    const int batchSize = 4;
    int initiatorPasses = 0;
    int responderPasses = 0;
    EnumerateReconciliationItemsDelegate enumerateInitiator = (visit, token) =>
    {
        initiatorPasses++;
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            token.ThrowIfCancellationRequested();
            visit(item.Span);
        }
    };
    EnumerateReconciliationItemsDelegate enumerateResponder = (visit, token) =>
    {
        responderPasses++;
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            token.ThrowIfCancellationRequested();
            visit(item.Span);
        }
    };
    using AntiEntropySession<string> initiator = useSnapshot
        ? new(AntiEntropyRole.Initiator, contract, enumerateInitiator, batchSize, BaseMemoryPool.Shared)
        : new(AntiEntropyRole.Initiator, contract, initiatorItems, batchSize, BaseMemoryPool.Shared);
    using AntiEntropySession<string> responder = useSnapshot
        ? new(AntiEntropyRole.Responder, contract, enumerateResponder, batchSize, BaseMemoryPool.Shared)
        : new(AntiEntropyRole.Responder, contract, responderItems, batchSize, BaseMemoryPool.Shared);

    ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
    {
        ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
        foreach(ReadOnlyMemory<byte> item in decoded)
        {
            if(initiatorDirectory.TryGetValue(Convert.ToHexString(item.Span), out string? element))
            {
                push.Add(new ReconciliationElementEntry<string>(item, element));
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
    ApplyReconciliationElementsDelegate<string> applyToInitiator = (entries, _, _) =>
    {
        foreach(ReconciliationElementEntry<string> entry in entries)
        {
            initiatorResult = initiatorResult.Add(entry.Element, initiatorReplica);
        }

        return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
    };
    ApplyReconciliationElementsDelegate<string> applyToResponder = (entries, _, _) =>
    {
        foreach(ReconciliationElementEntry<string> entry in entries)
        {
            responderResult = responderResult.Add(entry.Element, responderReplica);
        }

        return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
    };

    //The mirror decoder determines the exact batch count without polling the running sessions.
    ReconciliationCapacityHint capacity = new(items: 6, cells: 32);
    using ReconciliationEncoder local = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, capacity);
    using ReconciliationEncoder remote = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, capacity);
    using ReconciliationDecoder mirror = new(contract, BaseMemoryPool.Shared, capacity);
    foreach(ReadOnlyMemory<byte> item in initiatorItems)
    {
        local.Add(item.Span);
    }

    foreach(ReadOnlyMemory<byte> item in responderItems)
    {
        remote.Add(item.Span);
    }

    int symbols = 0;
    while(!mirror.IsComplete)
    {
        mirror.Absorb(local.ProduceNext().Combine(remote.ProduceNext()));
        symbols++;
    }

    Task initiatorRun = initiator.RunAsync((envelope, token) => ForwardReconciliationAsync(responder, envelope, token), resolve, null, applyToInitiator);
    Task responderRun = responder.RunAsync((envelope, token) => ForwardReconciliationAsync(initiator, envelope, token), null, serve, applyToResponder);
    int batches = (symbols + batchSize - 1) / batchSize;
    for(int batch = 0; batch < batches; batch++)
    {
        await responder.TriggerBatchAsync().ConfigureAwait(false);
    }

    await initiatorRun.ConfigureAwait(false);
    responder.Complete();
    await responderRun.ConfigureAwait(false);

    string[] expected = ["alpha", "beta", "delta", "epsilon", "gamma", "zeta"];
    Console.WriteLine($"INFO Anti-entropy {(useSnapshot ? "snapshot" : "incremental")}: {symbols} symbols; batches={batches}; differences={initiator.DecodedItems.Count}");

    return initiatorResult.Elements.Order(StringComparer.Ordinal).SequenceEqual(expected)
        && responderResult.Elements.Order(StringComparer.Ordinal).SequenceEqual(expected)
        && initiator.DecodedItems.Count == 3
        && initiator.State == AntiEntropySessionState.Completed
        && responder.State == AntiEntropySessionState.Completed
        && initiator.IsConverged && responder.IsConverged
        && (!useSnapshot || (initiatorPasses == batches && responderPasses == batches));
}

/// <summary>Forwards an envelope to the peer's in-memory channel and discards sends after peer completion.</summary>
static async ValueTask ForwardReconciliationAsync(AntiEntropySession<string> peer, ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
{
    try
    {
        await peer.SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
    }
    catch(ChannelClosedException)
    {
        //A completed session closes its inbound channel before all queued peer work has drained.
    }
}

/// <summary>Maps each pinned element's SHA-256 digest to the element served by the reconciliation exchange.</summary>
static Dictionary<string, string> ReconciliationDirectory(OrSet<string> set)
{
    Dictionary<string, string> directory = new(StringComparer.Ordinal);
    foreach(string element in set.Elements)
    {
        directory.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element))), element);
    }

    return directory;
}

/// <summary>Checks snapshot symbols against an incremental encoder across several range partitions.</summary>
static bool SnapshotStreamAgrees()
{
    ReconciliationContract contract = new(ReconciliationItemDomain.Structural, 8, 8,
        ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
    byte[][] items =
    [
        [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
        [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18],
        [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28],
    ];
    const int symbolCount = 33;
    using ReconciliationEncoder incremental = new(contract, ReconciliationInjectivityEnforcement.Strict,
        BaseMemoryPool.Shared, new ReconciliationCapacityHint(items.Length, symbolCount));
    foreach(byte[] item in items)
    {
        incremental.Add(item);
    }

    for(int index = 0; index < symbolCount; index++)
    {
        _ = incremental.ProduceNext();
    }

    foreach(int rangeSize in new[] { 1, 7, 33 })
    {
        int passes = 0;
        using ReconciliationSnapshotEncoder snapshot = new(contract, (visit, token) =>
        {
            passes++;
            foreach(byte[] item in items)
            {
                token.ThrowIfCancellationRequested();
                visit(item);
            }
        }, BaseMemoryPool.Shared, symbolCount);
        while(snapshot.ProducedCount < symbolCount)
        {
            snapshot.Produce(Math.Min(rangeSize, symbolCount - snapshot.ProducedCount), CancellationToken.None);
        }

        if(passes != (symbolCount + rangeSize - 1) / rangeSize)
        {
            return false;
        }

        for(int index = 0; index < symbolCount; index++)
        {
            ReconciliationSymbol expected = incremental.SymbolAt(index);
            ReconciliationSymbol actual = snapshot.SymbolAt(index);
            if(!expected.Sum.Span.SequenceEqual(actual.Sum.Span) || !expected.Checksum.Span.SequenceEqual(actual.Checksum.Span))
            {
                return false;
            }
        }
    }

    return true;
}

/// <summary>Compares the accelerated 128-bit backend and dispatched folds with scalar results at vector boundaries.</summary>
static bool XorAgrees()
{
    if(!ReconciliationXorVector128Backend.IsSupported)
    {
        return false;
    }

    foreach(int length in new[] { 0, 1, 2, 3, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 257 })
    {
        foreach(ulong seed in new[] { 0x1111111111111111UL, 0x2468ACE013579BDFUL })
        {
            byte[] scalar = XorBytes(length, seed);
            byte[] vector = XorBytes(length, seed);
            byte[] dispatched = XorBytes(length, seed);
            byte[] source = XorBytes(length, seed ^ 0xFFFFFFFFFFFFFFFFUL);
            ReconciliationXorScalarBackend.Fold(scalar, source);
            ReconciliationXorVector128Backend.Fold(vector, source);
            ReconciliationXor.Fold(dispatched, source);
            if(!scalar.AsSpan().SequenceEqual(vector) || !scalar.AsSpan().SequenceEqual(dispatched))
            {
                return false;
            }

            byte[] left = XorBytes(length, seed);
            byte[] right = XorBytes(length, seed ^ 0x0F0F0F0F0F0F0F0FUL);
            ReconciliationXorScalarBackend.Combine(left, right, scalar);
            ReconciliationXorVector128Backend.Combine(left, right, vector);
            if(!scalar.AsSpan().SequenceEqual(vector)
                || ReconciliationXorScalarBackend.IsNeutral(vector) != ReconciliationXorVector128Backend.IsNeutral(vector)
                || !ReconciliationXorVector128Backend.IsNeutral(new byte[length]))
            {
                return false;
            }
        }
    }

    return true;
}

/// <summary>Fills a byte array with the fixed linear congruential sequence used by XOR agreement tests.</summary>
static byte[] XorBytes(int length, ulong seed)
{
    byte[] bytes = new byte[length];
    ulong state = seed;
    for(int index = 0; index < bytes.Length; index++)
    {
        state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
        bytes[index] = (byte)(state >> 56);
    }

    return bytes;
}

/// <summary>
/// Verifies versioned register decisions and replicated log commits without elapsed-time triggers.
/// </summary>
/// <param name="check">The smoke result recorder.</param>
/// <returns>A task that completes after the consensus checks.</returns>
static async Task SmokeConsensusAsync(Action<string, bool> check)
{
    ImmutableArray<ReplicaId> replicas = [Replica(1), Replica(2), Replica(3)];
    ImmutableArray<HostId> members = [.. replicas.Select(replica => new HostId(replica, StoreIncarnation.FromSpan(replica.AsSpan()[..StoreIncarnation.Size])))];
    QuePaxaConfiguration genesis = QuePaxaConfiguration.CreateGenesis(members);
    QuePaxaVersionedNode<string>[] hosts = [.. members.Select(member => new QuePaxaVersionedNode<string>(genesis, member))];
    Dictionary<ReplicaId, VersionedRecorderEndpointDelegate<VersionedValue<string>>> endpoints = [];
    foreach(QuePaxaVersionedNode<string> host in hosts)
    {
        endpoints.Add(host.Self.Replica, (request, _) => ValueTask.FromResult(host.Handle(request)));
    }

    QuePaxaVersionedRegister<string> register = new(
        genesis,
        replicas[0],
        TimeSpan.Zero,
        member => endpoints[member],
        static () => new ProposalPriority(1),
        attemptsPerRecorder: 1,
        TimeProvider.System);
    QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("browser-register", CancellationToken.None).ConfigureAwait(false);
    check("QuePaxa three-node versioned decision",
        outcome.Status == QuePaxaWriteStatus.Committed
        && outcome.Version == RegisterVersion.First
        && outcome.Writer == replicas[0]
        && outcome.Value == "browser-register"
        && outcome.TookFastPath
        && outcome.DecidedAt == RecorderStep.RoundOnePhaseZero
        && outcome.Attempts == 1
        && outcome.Activated);

    if(register.Committed is { } committed)
    {
        foreach(QuePaxaVersionedNode<string> host in hosts)
        {
            _ = host.Learn(committed);
        }
    }

    check("QuePaxa committed record dissemination",
        hosts.All(host => host.Committed is { Version: var version, Value: "browser-register" }
            && version == RegisterVersion.First));

    RaftNode<string> leader = new(replicas[0], replicas);
    RaftNode<string>[] followers = [new(replicas[1], replicas), new(replicas[2], replicas)];
    RequestVoteRequest vote = leader.StartElection();
    foreach(RaftNode<string> follower in followers)
    {
        _ = leader.ReceiveVote(follower.Id, follower.HandleRequestVote(vote));
    }

    check("Raft three-node election", leader.Role == RaftRole.Leader && leader.CurrentTerm == Term.First);
    LogIndex proposed = leader.Propose("browser-log");
    ReplicateConsensusLog(leader, followers);
    //The next heartbeat carries the leader's committed index to both followers.
    ReplicateConsensusLog(leader, followers);
    check("Raft three-node commit",
        proposed == LogIndex.First
        && leader.CommitIndex == proposed
        && leader.Log.Count == 1
        && followers.All(follower => follower.CommitIndex == proposed
            && follower.Log.SequenceEqual(leader.Log)));

    RaftNode<string> runnerNode = new(replicas[0], [replicas[0]]);
    RaftRunner<string> runner = new(runnerNode);
    List<(LogIndex Index, string Command)> applied = [];
    await runner.TriggerElectionAsync().ConfigureAwait(false);
    Task<LogIndex> proposal = runner.ProposeAsync("browser-runner");
    //Completing the queue before starting its consumer makes draining the synchronization boundary.
    runner.Complete();
    await runner.RunAsync(
        static (_, _, _) => ValueTask.CompletedTask,
        applyCommitted: (index, command, _) =>
        {
            applied.Add((index, command));

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    LogIndex runnerIndex = await proposal.ConfigureAwait(false);
    check("Raft runner commit and apply",
        runnerIndex == LogIndex.First
        && runnerNode.CommitIndex == LogIndex.First
        && applied.Count == 1
        && applied[0] == (LogIndex.First, "browser-runner"));
}

/// <summary>
/// Delivers one append exchange to each follower in a fixed order.
/// </summary>
/// <param name="leader">The elected node sending the entries.</param>
/// <param name="followers">The nodes receiving the entries.</param>
static void ReplicateConsensusLog(RaftNode<string> leader, RaftNode<string>[] followers)
{
    foreach(RaftNode<string> follower in followers)
    {
        AppendEntriesRequest<string> request = leader.CreateAppendEntries(follower.Id);
        AppendEntriesReply reply = follower.HandleAppendEntries(request);
        leader.ReceiveAppendEntriesReply(follower.Id, reply);
    }
}

/// <summary>Carries a sequence number and payload through both channel formats.</summary>
/// <param name="Sequence">The message sequence number.</param>
/// <param name="Payload">The message content.</param>
internal sealed record SmokeMessage(int Sequence, string Payload);

/// <summary>Supplies explicit JSON metadata so the channel works in a trimmed wasm bundle.</summary>
[JsonSerializable(typeof(SmokeMessage))]
internal sealed partial class SmokeJsonContext: JsonSerializerContext;
