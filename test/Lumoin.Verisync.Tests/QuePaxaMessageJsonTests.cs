using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round trips, hostile input and byte pins for the QuePaxa message JSON codecs.
/// </summary>
/// <remarks>
/// <para>
/// EVERY HOSTILE VECTOR IS A COMPLETE, OTHERWISE-VALID PAYLOAD differing from a good one in exactly the field
/// under test. A fragment carrying only the field of interest dies on the first missing required field, which
/// makes the test green whatever the decoder does with the field it was written for, and a vector that fails
/// for the wrong reason reports coverage it does not have.
/// </para>
/// <para>
/// THREE ROUND TRIPS HERE EXIST TO HOLD A PROHIBITION SHUT rather than to cover ordinary behaviour. The codec
/// must add no validation of its own, and a rule the codec must NOT have cannot be caught by weakening
/// production code, because there is nothing to weaken. Only a legal payload that an added check would refuse
/// can catch it, so the all-zero identity, the step at the top of the range, and the reserved priority above
/// round one phase zero are pinned deliberately.
/// </para>
/// <para>
/// Payloads are written as templates with placeholder tokens rather than as interpolated strings, because a
/// JSON object ends in consecutive closing braces and an interpolated raw string reads those as its own
/// delimiters.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaMessageJsonTests
{
    private const string RequestTemplate = """{"step":$STEP,"proposal":{"priority":$PRIORITY,"owner":{"replica":"$REPLICA","lane":$LANE},"value":"v"}}""";
    private const string ReplyTemplate = """{"step":$STEP,"first":{"priority":$PRIORITY,"owner":{"replica":"$REPLICA","lane":$LANE},"value":"v"},"priorAggregate":null}""";

    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;
    private static RecorderStep Eight { get; } = RecorderStep.FromRoundAndPhase(2, 0);

    private static string ReplicaHex { get; } = Convert.ToHexStringLower(Replica(1).AsSpan());
    private static string ZeroReplicaHex { get; } = new('0', ReplicaId.Size * 2);


    [TestMethod]
    public void RequestRoundTrips()
    {
        var request = new RecordRequest<string>(Four, Proposal(ProposalPriority.Lowest, LaneA, "a value with spaces"));

        Assert.AreEqual(request, RoundTripRequest(request));
    }


    [TestMethod]
    public void ReplyWithPriorAggregateRoundTrips()
    {
        var reply = new RecordReply<string>(
            Eight,
            Proposal(new ProposalPriority(7), LaneA, "first"),
            Proposal(new ProposalPriority(3), LaneA, "prior"));

        Assert.AreEqual(reply, RoundTripReply(reply));
    }


    [TestMethod]
    public void ReplyWithoutPriorAggregateRoundTrips()
    {
        //A skipped step legitimately clears the prior aggregate, so a null there is ordinary rather than
        //malformed. The field is still written, as an explicit null, so a missing-field sweep can reach it.
        var reply = new RecordReply<string>(Eight, Proposal(new ProposalPriority(7), LaneA, "first"), null);

        RecordReply<string> back = RoundTripReply(reply);

        Assert.AreEqual(reply, back);
        Assert.IsNull(back.PriorAggregate);
    }


    //THE OBLIGATION ProposalPriority LEVIES ON ANY CODEC, and the pair is what makes it work. A pin at
    //Reserved alone is vacuous: a decoder reading the field as a double round-trips ulong.MaxValue intact,
    //because the double saturates back to it. Only the value one below, which collapses onto Reserved through
    //the same double, tells the two decoders apart.
    [TestMethod]
    public void ReservedPriorityRoundTripsExactly()
    {
        ulong[] priorities = [ProposalPriority.Lowest.Value, ProposalPriority.Reserved.Value - 1, ProposalPriority.Reserved.Value];
        foreach(ulong priority in priorities)
        {
            //The reserved priority is pinned ABOVE round one phase zero, which is the ordinary contended path
            //rather than an oddity: when the fast path fails the phase-zero template becomes the best of the
            //gathered first proposals, which may be the leader's own reserved one, and phases one to three
            //then send that template untouched. A decoder that refused it would deadlock the protocol.
            var request = new RecordRequest<string>(Eight, Proposal(new ProposalPriority(priority), LaneA, "v"));

            Assert.AreEqual(request, RoundTripRequest(request));
        }

        //The absent priority is the aggregate fold's identity. A request refuses it, so it is pinned on the
        //reply side only.
        var reply = new RecordReply<string>(Eight, Proposal(ProposalPriority.None, LaneA, "first"), null);

        Assert.AreEqual(reply, RoundTripReply(reply));
    }


    [TestMethod]
    public void ReservedAndTheValueBelowItStayDistinctAcrossTheWire()
    {
        var reserved = new RecordRequest<string>(Eight, Proposal(ProposalPriority.Reserved, LaneA, "v"));
        var below = new RecordRequest<string>(Eight, Proposal(new ProposalPriority(ProposalPriority.Reserved.Value - 1), LaneA, "v"));

        RecordRequest<string> reservedBack = RoundTripRequest(reserved);
        RecordRequest<string> belowBack = RoundTripRequest(below);

        Assert.AreNotEqual(reservedBack, belowBack);
        Assert.IsTrue(reservedBack.Proposal.Key.Priority.IsReserved);
        Assert.IsFalse(belowBack.Proposal.Key.Priority.IsReserved);
    }


    //A PROHIBITION PIN. The zero value of a proposer lane is degenerate rather than illegal, so a decoder must
    //not refuse the all-zero identity or lane zero.
    [TestMethod]
    public void TheAllZeroIdentityAtLaneZeroRoundTrips()
    {
        var request = new RecordRequest<string>(Four, Proposal(ProposalPriority.Lowest, default, "v"));

        RecordRequest<string> back = RoundTripRequest(request);

        Assert.AreEqual(request, back);
        Assert.AreEqual(0, back.Proposal.Key.Owner.Lane);
    }


    //A PROHIBITION PIN. The top of the step range is a legal wire value the protocol must accept, and it is
    //the only step at which a decoder doing step arithmetic misbehaves: RecorderStep.Next throws once the
    //budget is spent, and that exception is in the guard's caught set, so a genuine exhaustion would be
    //silently reported as a deserialization failure.
    [TestMethod]
    public void TheLastRepresentableStepRoundTrips()
    {
        var reply = new RecordReply<string>(RecorderStep.MaxValue, Proposal(ProposalPriority.Lowest, LaneA, "first"), null);

        RecordReply<string> back = RoundTripReply(reply);

        Assert.AreEqual(reply, back);
        Assert.IsTrue(back.Step.IsExhausted);
    }


    [TestMethod]
    public void MissingRequiredFieldsFailClosed()
    {
        //The request: its two fields, then each of the proposal's three, then each of the lane's two.
        RejectsRequest("""{"proposal":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":"v"}}""");
        RejectsRequest("""{"step":4}""");
        RejectsRequest("""{"step":4,"proposal":{"owner":{"replica":"$REPLICA","lane":0},"value":"v"}}""");
        RejectsRequest("""{"step":4,"proposal":{"priority":1,"value":"v"}}""");
        RejectsRequest("""{"step":4,"proposal":{"priority":1,"owner":{"replica":"$REPLICA","lane":0}}}""");
        RejectsRequest("""{"step":4,"proposal":{"priority":1,"owner":{"lane":0},"value":"v"}}""");
        RejectsRequest("""{"step":4,"proposal":{"priority":1,"owner":{"replica":"$REPLICA"},"value":"v"}}""");

        //The reply's three fields, the prior aggregate included, because it is written as an explicit null
        //rather than omitted and is therefore required present.
        RejectsReply("""{"first":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":"v"},"priorAggregate":null}""");
        RejectsReply("""{"step":4,"priorAggregate":null}""");
        RejectsReply("""{"step":4,"first":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":"v"}}""");
    }


    //A MISSING FIELD MUST BE REFUSED BY NAME, AND ONLY THE INNER CAUSE SAYS WHETHER IT WAS. The uniform
    //MessageDeserializationException is raised either way: a decoder that let an absent property through would
    //carry an undefined element to the next accessor, which throws on the wrong kind and is caught by the same
    //guard. The two are indistinguishable from outside, so the outer type proves nothing about which field was
    //missing, and a channel operator reading the log gets a value-kind complaint instead of the field name.
    [TestMethod]
    public void AMissingFieldIsRefusedByNameRatherThanByTheNextAccessor()
    {
        MessageDeserializationException failure = Assert.ThrowsExactly<MessageDeserializationException>(
            () => DeserializeRequest("""{"step":4}"""));

        Assert.IsInstanceOfType<JsonException>(failure.InnerException);
        Assert.Contains("proposal", failure.InnerException!.Message);
        Assert.Contains("A record request", failure.InnerException.Message);

        //The prior aggregate is the field this matters most for. It is written as an explicit null rather than
        //omitted precisely so that an absent one is malformed, and an absent one that fell through to the
        //proposal reader would still be refused, but for the wrong reason and without naming the field.
        MessageDeserializationException absentPriorAggregate = Assert.ThrowsExactly<MessageDeserializationException>(
            () => DeserializeReply(Resolve("""{"step":4,"first":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":"v"}}""")));

        Assert.IsInstanceOfType<JsonException>(absentPriorAggregate.InnerException);
        Assert.Contains("priorAggregate", absentPriorAggregate.InnerException!.Message);
        Assert.Contains("A record reply", absentPriorAggregate.InnerException.Message);
    }


    //THE VECTOR THAT PROVES THE DECODER IS NOT ON A DOUBLE PATH. GetUInt64 refuses a token carrying a fraction
    //or an exponent, so both payloads fail closed; a decoder reaching for GetDouble would accept them and
    //quietly demote the reserved priority. Every other field is well formed, so the priority read is the only
    //thing that can fail.
    [TestMethod]
    public void ADoubleFormPriorityIsRejected()
    {
        RejectsRequest(Request(step: "4", priority: "18446744073709551615.0", replica: "$REPLICA", lane: "0"));
        RejectsRequest(Request(step: "4", priority: "1.8446744073709552e19", replica: "$REPLICA", lane: "0"));

        //One above the range is not representable either, and it fails the same way rather than wrapping.
        RejectsRequest(Request(step: "4", priority: "18446744073709551616", replica: "$REPLICA", lane: "0"));
    }


    //THE MESSAGE-LEVEL STEP FLOOR, which nothing else reaches. A negative step and one past the top of the
    //range are refused by RecorderStep itself before either message constructor runs, so only a step that is a
    //legal RecorderStep and an illegal message step can prove the decoder re-runs the message's own rule.
    [TestMethod]
    public void AStepBelowRoundOnePhaseZeroIsRejected()
    {
        foreach(string step in (string[])["0", "3"])
        {
            RejectsRequest(Request(step, priority: "1", replica: "$REPLICA", lane: "0"));
            RejectsReply(Reply(step, priority: "1", replica: "$REPLICA", lane: "0"));
        }
    }


    [TestMethod]
    public void AStepOutsideTheRepresentableRangeIsRejected()
    {
        foreach(string step in (string[])["-1", "1028"])
        {
            RejectsRequest(Request(step, priority: "1", replica: "$REPLICA", lane: "0"));
            RejectsReply(Reply(step, priority: "1", replica: "$REPLICA", lane: "0"));
        }
    }


    [TestMethod]
    public void ANegativeLaneIsRejected()
    {
        RejectsRequest(Request(step: "4", priority: "1", replica: "$REPLICA", lane: "-1"));
    }


    //THE IDENTITY WIDTH, and the hex counts are EVEN deliberately. Convert.FromHexString refuses an odd length
    //before ReplicaId.FromSpan ever runs, so an odd-length vector would test hex formatting while appearing to
    //test the width. Sixty-two and sixty-six characters are thirty-one and thirty-three bytes, which decode
    //cleanly and then reach the width guard.
    [TestMethod]
    public void AWrongWidthIdentityIsRejected()
    {
        foreach(int characters in (int[])[62, 66])
        {
            RejectsRequest(Request(step: "4", priority: "1", replica: new string('a', characters), lane: "0"));
        }

        //An odd length is rejected too, by the hex parse rather than by the width rule.
        RejectsRequest(Request(step: "4", priority: "1", replica: new string('a', 63), lane: "0"));
    }


    [TestMethod]
    public void ARequestCarryingTheAbsentPriorityIsRejected()
    {
        //The absent priority is the aggregate fold's identity: it is never drawn and never sent, and a request
        //carrying it would put the identity element on the wire.
        RejectsRequest(Request(step: "4", priority: "0", replica: "$REPLICA", lane: "0"));
    }


    [TestMethod]
    public void TrailingDataIsRejected()
    {
        Assert.Throws<MessageDeserializationException>(
            () => DeserializeRequest(Resolve(Request(step: "4", priority: "1", replica: "$REPLICA", lane: "0") + """{"step":4}""")));
    }


    [TestMethod]
    public void ALiteralNullPayloadIsRejected()
    {
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRequest("null"));
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeReply("null"));
    }


    [TestMethod]
    public void ATruncatedPayloadIsRejected()
    {
        Assert.Throws<MessageDeserializationException>(() => DeserializeRequest("""{"step":4,"proposal":{"priori"""));
    }


    //THE ENCODING IS A SPECIFICATION RATHER THAN AN ARTIFACT ONLY IF SOMETHING PINS THE BYTES. Nothing else in
    //the suite would notice a renamed field or a reordered object, because a round trip re-reads whatever it
    //wrote.
    [TestMethod]
    public void TheRequestEncodingIsPinned()
    {
        var request = new RecordRequest<string>(Four, Proposal(ProposalPriority.Reserved, default, "v"));

        Assert.AreEqual(
            Request(step: "4", priority: "18446744073709551615", replica: ZeroReplicaHex, lane: "0"),
            SerializeRequest(request));
    }


    [TestMethod]
    public void TheReplyEncodingIsPinned()
    {
        var reply = new RecordReply<string>(Four, Proposal(ProposalPriority.Lowest, default, "v"), null);

        Assert.AreEqual(
            Reply(step: "4", priority: "1", replica: ZeroReplicaHex, lane: "0"),
            SerializeReply(reply));
    }


    private static string Request(string step, string priority, string replica, string lane)
    {
        return Fill(RequestTemplate, step, priority, replica, lane);
    }


    private static string Reply(string step, string priority, string replica, string lane)
    {
        return Fill(ReplyTemplate, step, priority, replica, lane);
    }


    private static string Fill(string template, string step, string priority, string replica, string lane)
    {
        return template
            .Replace("$STEP", step, StringComparison.Ordinal)
            .Replace("$PRIORITY", priority, StringComparison.Ordinal)
            .Replace("$REPLICA", replica, StringComparison.Ordinal)
            .Replace("$LANE", lane, StringComparison.Ordinal);
    }


    private static string Resolve(string json) => json.Replace("$REPLICA", ReplicaHex, StringComparison.Ordinal);


    private static void RejectsRequest(string json)
    {
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRequest(Resolve(json)));
    }


    private static void RejectsReply(string json)
    {
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeReply(Resolve(json)));
    }


    private static PrioritizedProposal<string> Proposal(ProposalPriority priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(priority, owner), value);
    }


    private static string SerializeRequest(RecordRequest<string> request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        QuePaxaMessageJson.CreateRequestSerializer<string>(WriteString)(request, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static string SerializeReply(RecordReply<string> reply)
    {
        var buffer = new ArrayBufferWriter<byte>();
        QuePaxaMessageJson.CreateReplySerializer<string>(WriteString)(reply, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static RecordRequest<string> RoundTripRequest(RecordRequest<string> request)
    {
        return DeserializeRequest(SerializeRequest(request));
    }


    private static RecordReply<string> RoundTripReply(RecordReply<string> reply)
    {
        return DeserializeReply(SerializeReply(reply));
    }


    private static RecordRequest<string> DeserializeRequest(string json)
    {
        return QuePaxaMessageJson.CreateRequestDeserializer(ReadString)(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static RecordReply<string> DeserializeReply(string json)
    {
        return QuePaxaMessageJson.CreateReplyDeserializer(ReadString)(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadString(JsonElement element) => element.GetString()!;


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
