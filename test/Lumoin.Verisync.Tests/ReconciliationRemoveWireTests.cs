using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Envelope-factory and JSON-codec coverage for the remove-aware wire payloads: the causal-context exchange
/// (<see cref="ReconciliationContext"/>), the remove push (<see cref="ReconciliationDrop"/>), and the session
/// completion frame (<see cref="ReconciliationCompletion"/>). The factories must each set exactly one slot and
/// leave the other seven null, mirroring the landed payloads — the context and drop slot tests now also assert
/// the completion slot stays null, so a new slot cannot slip past their census. The codec round-trips each: a
/// context is compared by reconstructing its <see cref="VectorClock"/> with <see cref="VectorClock.FromState"/>
/// (value equality), not by record reference equality; a drop is compared by its dots as a set, because the
/// underlying <see cref="ImmutableArray{T}"/> members compare by reference under synthesized record equality;
/// and a completion round-trips its transfer count by record value equality. Malformed context, drop, and
/// completion frames fail closed as <see cref="MessageDeserializationException"/>, and a negative transfer
/// count is rejected at construction, mirroring the existing codec strictness vectors.
/// </summary>
[TestClass]
internal sealed class ReconciliationRemoveWireTests
{
    //The context and drop frames carry no item-width fields, so any pinned contract deserializes them; the
    //content-hash default matches the 32-byte replica bytes these payloads encode.
    private static ReconciliationContract LocalContract { get; } = ReconciliationContract.ContentHashDefault;


    [TestMethod]
    public void ForContextSetsOnlyTheContextSlot()
    {
        ReconciliationContext context = new(ClockState((1, 3), (2, 5)));

        ReconciliationEnvelope<string> envelope = ReconciliationEnvelope<string>.ForContext(context);

        Assert.AreEqual(context, envelope.Context);
        Assert.IsNotNull(envelope.Context);
        Assert.IsNull(envelope.Offer);
        Assert.IsNull(envelope.Symbols);
        Assert.IsNull(envelope.Done);
        Assert.IsNull(envelope.Fetch);
        Assert.IsNull(envelope.Elements);
        Assert.IsNull(envelope.Drop);
        Assert.IsNull(envelope.Completion);
    }


    [TestMethod]
    public void ForDropSetsOnlyTheDropSlot()
    {
        ReconciliationDrop drop = new([new DotState(ReplicaBytes(1), 3), new DotState(ReplicaBytes(2), 5)]);

        ReconciliationEnvelope<string> envelope = ReconciliationEnvelope<string>.ForDrop(drop);

        Assert.AreEqual(drop, envelope.Drop);
        Assert.IsNotNull(envelope.Drop);
        Assert.IsNull(envelope.Offer);
        Assert.IsNull(envelope.Symbols);
        Assert.IsNull(envelope.Done);
        Assert.IsNull(envelope.Fetch);
        Assert.IsNull(envelope.Elements);
        Assert.IsNull(envelope.Context);
        Assert.IsNull(envelope.Completion);
    }


    [TestMethod]
    public void ForCompletionSetsOnlyTheCompletionSlot()
    {
        ReconciliationCompletion completion = new(2);

        ReconciliationEnvelope<string> envelope = ReconciliationEnvelope<string>.ForCompletion(completion);

        Assert.AreEqual(completion, envelope.Completion);
        Assert.IsNotNull(envelope.Completion);
        Assert.IsNull(envelope.Offer);
        Assert.IsNull(envelope.Symbols);
        Assert.IsNull(envelope.Done);
        Assert.IsNull(envelope.Fetch);
        Assert.IsNull(envelope.Elements);
        Assert.IsNull(envelope.Context);
        Assert.IsNull(envelope.Drop);
    }


    [TestMethod]
    public void ContextEnvelopeRoundTripsByReconstructedClockValue()
    {
        //Build the original clock as a value, take its state, ship the state, and compare the decoded state by
        //reconstructing both clocks — the reconstruct-to-compare convention, since the state record compares its
        //ImmutableArray member by reference.
        VectorClock original = VectorClock.Empty.Increment(Replica(1)).Increment(Replica(1)).Increment(Replica(2));
        ReconciliationContext context = new(original.ToState());

        ReconciliationEnvelope<string> back = RoundTrip(ReconciliationEnvelope<string>.ForContext(context));

        Assert.IsNotNull(back.Context);
        VectorClock decoded = VectorClock.FromState(back.Context.Clock);
        Assert.AreEqual(original, decoded);
    }


    [TestMethod]
    public void EmptyContextEnvelopeRoundTripsToTheEmptyClock()
    {
        //A fresh replica's context is the empty clock; it must survive the wire as the empty clock.
        ReconciliationContext context = new(VectorClock.Empty.ToState());

        ReconciliationEnvelope<string> back = RoundTrip(ReconciliationEnvelope<string>.ForContext(context));

        Assert.IsNotNull(back.Context);
        Assert.AreEqual(VectorClock.Empty, VectorClock.FromState(back.Context.Clock));
    }


    [TestMethod]
    public void DropEnvelopeRoundTripsItsDotsAsASet()
    {
        DotState first = new(ReplicaBytes(1), 3);
        DotState second = new(ReplicaBytes(2), 5);
        DotState third = new(ReplicaBytes(3), 9);
        ReconciliationDrop drop = new([first, second, third]);

        ReconciliationEnvelope<string> back = RoundTrip(ReconciliationEnvelope<string>.ForDrop(drop));

        Assert.IsNotNull(back.Drop);

        //Compare the dots as a set: a drop's custom equality is order-independent over (replica, counter) pairs,
        //so the decoded drop must equal the original regardless of any reordering on the wire. The decoded dots
        //are freshly built DotState values, whose synthesized equality compares the replica array by reference,
        //so the comparison goes through the drop's set equality and the reconstructed dot pairs, never DotState
        //reference identity.
        Assert.AreEqual(drop, back.Drop);
        Assert.HasCount(3, back.Drop.Dots);

        HashSet<(string Replica, int Counter)> decodedPairs = [.. back.Drop.Dots.Select(dot => (Convert.ToHexStringLower(dot.Replica.AsSpan()), dot.Counter))];
        Assert.Contains((Convert.ToHexStringLower(first.Replica.AsSpan()), first.Counter), decodedPairs);
        Assert.Contains((Convert.ToHexStringLower(second.Replica.AsSpan()), second.Counter), decodedPairs);
        Assert.Contains((Convert.ToHexStringLower(third.Replica.AsSpan()), third.Counter), decodedPairs);
    }


    [TestMethod]
    public void MalformedContextFrameFailsClosed()
    {
        //A context payload that is not an object cannot carry a clock and fails closed before any field is read.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"context","payload":42}"""));

        //A context document cut off mid-frame cannot parse; the reader exception is wrapped as MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(() => Deserialize("""{"type":"context","payload":{"entries":["""));
    }


    [TestMethod]
    public void MalformedDropFrameFailsClosed()
    {
        //A drop payload that is not an object cannot carry dots and fails closed before any field is read.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"drop","payload":42}"""));

        //A drop document cut off mid-frame cannot parse; the reader exception is wrapped as MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(() => Deserialize("""{"type":"drop","payload":{"dots":["""));
    }


    [TestMethod]
    public void CompletionEnvelopeRoundTripsItsTransferCount()
    {
        //A transfer count survives the wire as its exact value; the record compares by value, so the decoded
        //completion equals the original.
        ReconciliationCompletion completion = new(2);
        ReconciliationEnvelope<string> back = RoundTrip(ReconciliationEnvelope<string>.ForCompletion(completion));

        Assert.IsNotNull(back.Completion);
        Assert.AreEqual(completion, back.Completion);
        Assert.AreEqual(2, back.Completion.TransferCount);
    }


    [TestMethod]
    public void QuiescentCompletionEnvelopeRoundTripsTheZeroTransferCount()
    {
        //Zero is the quiescent-exchange cardinality and is legal and meaningful; it must survive the wire.
        ReconciliationEnvelope<string> back = RoundTrip(ReconciliationEnvelope<string>.ForCompletion(new ReconciliationCompletion(0)));

        Assert.IsNotNull(back.Completion);
        Assert.AreEqual(0, back.Completion.TransferCount);
    }


    [TestMethod]
    public void MalformedCompletionFrameFailsClosed()
    {
        //A completion payload that is not an object cannot carry a transfer count and fails closed before any field is read.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"completion","payload":42}"""));

        //A completion document cut off mid-frame cannot parse; the reader exception is wrapped as MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(() => Deserialize("""{"type":"completion","payload":{"transferCount":"""));
    }


    [TestMethod]
    public void ANegativeTransferCountIsRejectedAtConstruction()
    {
        //A negative transfer count is never a legal cardinality; construction fails closed, as the done count does.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCompletion(-1));
    }


    private static ReconciliationEnvelope<string> RoundTrip(ReconciliationEnvelope<string> envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ReconciliationJson.CreateEnvelopeSerializer<string>((writer, value) => writer.WriteStringValue(value))(envelope, buffer);

        return ReconciliationJson.CreateEnvelopeDeserializer<string>(LocalContract, element => element.GetString()!)(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static ReconciliationEnvelope<string> Deserialize(string json)
    {
        return ReconciliationJson.CreateEnvelopeDeserializer<string>(LocalContract, element => element.GetString()!)(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    //Builds a vector clock state from (replica seed, count) pairs directly, without going through a live clock.
    private static VectorClockState ClockState(params (byte Seed, int Count)[] entries)
    {
        ImmutableArray<ReplicaCounterEntry>.Builder builder = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(entries.Length);
        foreach((byte seed, int count) in entries)
        {
            builder.Add(new ReplicaCounterEntry(ReplicaBytes(seed), count));
        }

        return new VectorClockState(builder.ToImmutable());
    }


    //Builds a deterministic replica id with the seed byte at position zero, without System.Random (CA5394).
    private static ReplicaId Replica(byte seed)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = seed;

        return ReplicaId.FromSpan(buffer);
    }


    //Builds the fixed 32-byte (ReplicaId.Size) replica bytes for a deterministic id, without System.Random.
    private static ImmutableArray<byte> ReplicaBytes(byte seed)
    {
        byte[] bytes = new byte[ReplicaId.Size];
        bytes[0] = seed;

        return ImmutableArray.Create(bytes);
    }
}
