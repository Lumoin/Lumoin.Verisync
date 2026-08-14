using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips and hostile-input checks for the Raft envelope and node-state JSON codecs, mirroring
/// <see cref="ConsensusMessageJsonTests"/>: every wire kind survives a serialize/deserialize cycle and every
/// malformed payload the spec enumerates fails closed — on the deserialize path as the uniform
/// <see cref="MessageDeserializationException"/>, on the serialize path as a <see cref="JsonException"/>.
/// </summary>
[TestClass]
internal sealed class RaftJsonTests
{
    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);


    [TestMethod]
    public void VoteRequestEnvelopeRoundTrips()
    {
        //A candidate's vote solicitation survives the wire with its term and log shape intact.
        RaftEnvelope<int> envelope = RaftEnvelope<int>.ForVoteRequest(N1, new RequestVoteRequest(new Term(3), N1, new LogIndex(2), Term.First));

        RaftEnvelope<int> back = RoundTripEnvelope(envelope);

        Assert.AreEqual(N1, back.From);
        Assert.AreEqual(envelope.VoteRequest, back.VoteRequest);
        Assert.IsNull(back.VoteReply);
        Assert.IsNull(back.AppendRequest);
        Assert.IsNull(back.AppendReply);
    }


    [TestMethod]
    public void VoteReplyEnvelopeRoundTrips()
    {
        //A granted vote reply carries the voter's term and the grant bit across the wire.
        RaftEnvelope<int> envelope = RaftEnvelope<int>.ForVoteReply(N2, new RequestVoteReply(new Term(3), true));

        RaftEnvelope<int> back = RoundTripEnvelope(envelope);

        Assert.AreEqual(N2, back.From);
        Assert.AreEqual(envelope.VoteReply, back.VoteReply);
    }


    [TestMethod]
    public void AppendRequestEnvelopeWithEntriesRoundTrips()
    {
        //A non-heartbeat append with entries round-trips, including each entry's term and command value.
        AppendEntriesRequest<int> request = new(
            new Term(5), N1, LogIndex.First, new Term(2), [new RaftLogEntry<int>(new Term(4), 41), new RaftLogEntry<int>(new Term(5), 42)], LogIndex.First);
        RaftEnvelope<int> envelope = RaftEnvelope<int>.ForAppendRequest(N1, request);

        RaftEnvelope<int> back = RoundTripEnvelope(envelope);

        Assert.AreEqual(N1, back.From);
        Assert.IsNotNull(back.AppendRequest);
        Assert.AreEqual(request.Term, back.AppendRequest.Term);
        Assert.AreEqual(request.LeaderId, back.AppendRequest.LeaderId);
        Assert.AreEqual(request.PrevLogIndex, back.AppendRequest.PrevLogIndex);
        Assert.AreEqual(request.PrevLogTerm, back.AppendRequest.PrevLogTerm);
        Assert.AreEqual(request.LeaderCommit, back.AppendRequest.LeaderCommit);

        //The record's ImmutableArray member compares by reference, so the entries are compared by content.
        Assert.AreSequenceEqual(ToEntryArray(request.Entries), ToEntryArray(back.AppendRequest.Entries));
    }


    [TestMethod]
    public void AppendRequestEnvelopeAsHeartbeatRoundTrips()
    {
        //An empty-entries heartbeat is the degenerate append; the empty array must survive intact.
        AppendEntriesRequest<int> heartbeat = new(new Term(5), N1, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst);
        RaftEnvelope<int> envelope = RaftEnvelope<int>.ForAppendRequest(N1, heartbeat);

        RaftEnvelope<int> back = RoundTripEnvelope(envelope);

        Assert.IsNotNull(back.AppendRequest);
        Assert.HasCount(0, back.AppendRequest.Entries);
        Assert.AreEqual(heartbeat.Term, back.AppendRequest.Term);
        Assert.AreEqual(heartbeat.LeaderId, back.AppendRequest.LeaderId);
        Assert.AreEqual(heartbeat.PrevLogIndex, back.AppendRequest.PrevLogIndex);
        Assert.AreEqual(heartbeat.PrevLogTerm, back.AppendRequest.PrevLogTerm);
        Assert.AreEqual(heartbeat.LeaderCommit, back.AppendRequest.LeaderCommit);
    }


    [TestMethod]
    public void AppendReplyEnvelopeRoundTrips()
    {
        //A successful append reply carries the follower's term, success bit, and resulting match index.
        RaftEnvelope<int> envelope = RaftEnvelope<int>.ForAppendReply(N2, new AppendEntriesReply(new Term(5), true, new LogIndex(3)));

        RaftEnvelope<int> back = RoundTripEnvelope(envelope);

        Assert.AreEqual(N2, back.From);
        Assert.AreEqual(envelope.AppendReply, back.AppendReply);
    }


    [TestMethod]
    public void NodeStateRoundTripsWithVoteAndLog()
    {
        //The durable triple round-trips: the term, a cast vote (hex), and the log of int commands.
        RaftNodeState<int> state = new(new Term(4), [.. N1.AsSpan()], [new RaftLogEntry<int>(new Term(2), 7), new RaftLogEntry<int>(new Term(4), 9)]);

        RaftNodeState<int> back = RoundTripState(state);

        Assert.AreEqual(state.CurrentTerm, back.CurrentTerm);
        Assert.AreSequenceEqual(state.VotedFor.ToArray(), back.VotedFor.ToArray());
        Assert.HasCount(2, back.Log);
        Assert.AreEqual(9, back.Log[1].Command);
    }


    [TestMethod]
    public void NodeStateWithNoVoteRoundTripsAsAnEmptyVotedFor()
    {
        //An absent vote is written as a null votedFor and reads back as the empty "no vote" encoding.
        RaftNodeState<int> state = new(Term.Zero, [], []);

        RaftNodeState<int> back = RoundTripState(state);

        Assert.HasCount(0, back.VotedFor);
        Assert.HasCount(0, back.Log);
        Assert.AreEqual(Term.Zero, back.CurrentTerm);
    }


    [TestMethod]
    public void UnknownEnvelopeTypeIsRejected()
    {
        //An unrecognized discriminator is a malformed envelope and must fail closed.
        string json = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"evil","payload":{}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void BadReplicaHexIsRejected()
    {
        //A non-hex from field cannot decode to a replica id; reject rather than guess.
        string json = """{"from":"zz","type":"voteReply","payload":{"term":1,"voteGranted":true}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void WrongLengthReplicaIdIsRejected()
    {
        //The from hex must decode to exactly one replica id width; a short id is rejected.
        string json = """{"from":"0102","type":"voteReply","payload":{"term":1,"voteGranted":true}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void NegativeTermIsRejected()
    {
        //Terms are non-negative everywhere on the wire; a negative term is forged.
        string json = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteReply","payload":{"term":-1,"voteGranted":true}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void ZeroLogEntryTermIsRejected()
    {
        //A real log entry's term is at least one; term zero is the term a node holds before any election. The
        //codec carries no rule of its own here, so this pins that RaftLogEntry's own validator runs on the
        //decode path and reaches a reader as the uniform failure.
        string json = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"appendRequest","payload":{"term":2,"leaderId":"0100000000000000000000000000000000000000000000000000000000000000","prevLogIndex":0,"prevLogTerm":0,"entries":[{"term":0,"command":1}],"leaderCommit":0}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void ATermAboveTheExactlyRepresentableRangeIsRejected()
    {
        //Two to the fifty-third and above cannot survive a double-parsing consumer intact, and a term that
        //two readers disagree about is one a comparison rule reads differently at either end of the wire.
        string json = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteReply","payload":{"term":9007199254740992,"voteGranted":true}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));

        //One below the bound is legal and decodes, so the refusal above is the cap and not a width limit the
        //accessor imposes.
        string atBound = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteReply","payload":{"term":9007199254740991,"voteGranted":true}}""";

        Assert.AreEqual(Term.MaxValue, DeserializeEnvelope(atBound).VoteReply!.Term);
    }


    [TestMethod]
    public void NegativeMatchIndexIsRejected()
    {
        //A match index is a non-negative log position; a negative one is malformed.
        string json = """{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"appendReply","payload":{"term":2,"success":true,"matchIndex":-1}}""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void TruncatedPayloadIsRejected()
    {
        //A cut-off document cannot parse; JsonDocument.Parse throws a JsonReaderException, which the codec
        //wraps as the uniform MessageDeserializationException.
        string json = """{"from":"0100000000000000000000000000000000000000""";

        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope(json));
    }


    [TestMethod]
    public void EachFactoryProducesExactlyOnePayload()
    {
        //Two payloads is unrepresentable on the wire, so the invariant is asserted at construction: every
        //factory yields an envelope with exactly one non-null payload and the others null.
        RaftEnvelope<int> voteRequest = RaftEnvelope<int>.ForVoteRequest(N1, new RequestVoteRequest(Term.First, N1, LogIndex.BeforeFirst, Term.Zero));
        RaftEnvelope<int> voteReply = RaftEnvelope<int>.ForVoteReply(N1, new RequestVoteReply(Term.First, true));
        RaftEnvelope<int> appendRequest = RaftEnvelope<int>.ForAppendRequest(N1, new AppendEntriesRequest<int>(Term.First, N1, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));
        RaftEnvelope<int> appendReply = RaftEnvelope<int>.ForAppendReply(N1, new AppendEntriesReply(Term.First, true, LogIndex.BeforeFirst));

        AssertExactlyOnePayload(voteRequest);
        AssertExactlyOnePayload(voteReply);
        AssertExactlyOnePayload(appendRequest);
        AssertExactlyOnePayload(appendReply);
    }


    [TestMethod]
    public void SerializingAnEnvelopeWithTwoPayloadsFailsClosed()
    {
        //A hand-built envelope that violates the single-payload invariant has no legal wire shape; the codec
        //refuses it rather than emit an ambiguous frame.
        RaftEnvelope<int> malformed = new(N1, new RequestVoteRequest(Term.First, N1, LogIndex.BeforeFirst, Term.Zero), new RequestVoteReply(Term.First, true), null, null);

        SerializeMessageDelegate<RaftEnvelope<int>> serialize = RaftJson.CreateEnvelopeSerializer<int>((writer, value) => writer.WriteNumberValue(value));

        Assert.Throws<JsonException>(() => serialize(malformed, new ArrayBufferWriter<byte>()));
    }


    [TestMethod]
    public void SerializingAnEnvelopeWithNoPayloadFailsClosed()
    {
        //An empty envelope likewise has no wire shape: exactly one payload must always be present.
        RaftEnvelope<int> empty = new(N1, null, null, null, null);

        SerializeMessageDelegate<RaftEnvelope<int>> serialize = RaftJson.CreateEnvelopeSerializer<int>((writer, value) => writer.WriteNumberValue(value));

        Assert.Throws<JsonException>(() => serialize(empty, new ArrayBufferWriter<byte>()));
    }


    [TestMethod]
    public void MissingRequiredFieldsFailClosed()
    {
        //A required field absent from an otherwise well-formed object must fail closed as MessageDeserializationException, not
        //surface the framework's KeyNotFoundException from a raw property accessor. One omission per arm.

        //The envelope's own three fields.
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"type":"voteReply","payload":{"term":1,"voteGranted":true}}"""));
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","payload":{"term":1,"voteGranted":true}}"""));
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteReply"}"""));

        //A vote request missing its candidate id.
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteRequest","payload":{"term":3,"lastLogIndex":2,"lastLogTerm":1}}"""));

        //A vote reply missing its grant bit.
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"voteReply","payload":{"term":1}}"""));

        //An append request missing its entries array, then a log entry missing its command.
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"appendRequest","payload":{"term":2,"leaderId":"0100000000000000000000000000000000000000000000000000000000000000","prevLogIndex":0,"prevLogTerm":0,"leaderCommit":0}}"""));
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"appendRequest","payload":{"term":2,"leaderId":"0100000000000000000000000000000000000000000000000000000000000000","prevLogIndex":0,"prevLogTerm":0,"entries":[{"term":2}],"leaderCommit":0}}"""));

        //An append reply missing its success bit.
        Assert.Throws<MessageDeserializationException>(() => DeserializeEnvelope("""{"from":"0100000000000000000000000000000000000000000000000000000000000000","type":"appendReply","payload":{"term":2,"matchIndex":3}}"""));

        //The durable node state's three fields.
        Assert.Throws<MessageDeserializationException>(() => DeserializeState("""{"votedFor":null,"log":[]}"""));
        Assert.Throws<MessageDeserializationException>(() => DeserializeState("""{"currentTerm":0,"log":[]}"""));
        Assert.Throws<MessageDeserializationException>(() => DeserializeState("""{"currentTerm":0,"votedFor":null}"""));
    }


    private static void AssertExactlyOnePayload(RaftEnvelope<int> envelope)
    {
        int present = 0;
        if(envelope.VoteRequest is not null)
        {
            present++;
        }

        if(envelope.VoteReply is not null)
        {
            present++;
        }

        if(envelope.AppendRequest is not null)
        {
            present++;
        }

        if(envelope.AppendReply is not null)
        {
            present++;
        }

        Assert.AreEqual(1, present);
    }


    private static RaftEnvelope<int> RoundTripEnvelope(RaftEnvelope<int> envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        RaftJson.CreateEnvelopeSerializer<int>((writer, value) => writer.WriteNumberValue(value))(envelope, buffer);

        return RaftJson.CreateEnvelopeDeserializer<int>(element => element.GetInt32())(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static RaftNodeState<int> RoundTripState(RaftNodeState<int> state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        RaftJson.CreateNodeStateSerializer<int>((writer, value) => writer.WriteNumberValue(value))(state, buffer);

        return RaftJson.CreateNodeStateDeserializer<int>(element => element.GetInt32())(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static RaftEnvelope<int> DeserializeEnvelope(string json)
    {
        return RaftJson.CreateEnvelopeDeserializer<int>(element => element.GetInt32())(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static RaftNodeState<int> DeserializeState(string json)
    {
        return RaftJson.CreateNodeStateDeserializer<int>(element => element.GetInt32())(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static RaftLogEntry<int>[] ToEntryArray(ImmutableArray<RaftLogEntry<int>> entries)
    {
        var array = new RaftLogEntry<int>[entries.Length];
        entries.CopyTo(array);

        return array;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
