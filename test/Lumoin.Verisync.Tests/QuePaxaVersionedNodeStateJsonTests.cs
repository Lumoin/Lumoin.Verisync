using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round trips, hostile input and byte pins for the versioned host's durable state, which is not a message but
/// shares the codecs' validation split.
/// </summary>
/// <remarks>
/// <para>
/// EVERY HOSTILE VECTOR IS A COMPLETE, OTHERWISE-VALID PAYLOAD differing from a good one in exactly the field
/// under test, and each asserts on the inner exception so that it fails for the reason it was written for
/// rather than on a missing field it never reached.
/// </para>
/// <para>
/// THE SPLIT IS PINNED FROM THE ACCEPTING SIDE AS WELL AS THE REFUSING ONE. The decoder carries no rule that
/// reads two fields at once, so a payload whose stored leader or stored version disagrees with the record beside
/// it decodes into a state, and <see cref="QuePaxaVersionedNode{TValue}.FromState"/> is what refuses the very
/// same value. That half is the one a cross-check moved into the codec would break.
/// </para>
/// <para>
/// Payloads are written as templates with placeholder tokens rather than as interpolated strings, because a
/// JSON object ends in consecutive closing braces and an interpolated raw string reads those as its own
/// delimiters. No token is a prefix of another within one template, so the order of substitution carries no
/// meaning.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaVersionedNodeStateJsonTests
{
    private const string NodeStateTemplate = """{"host":$WHOSE,"committed":$COMMITTED,"recorderVersion":$SERVES,"configuredLeader":$LEADER,"activeConfiguration":$ACTIVE,"recorder":$REGISTER}""";
    private const string RecordTemplate = """{"version":$AT,"writer":"$BY","configuration":$UNDER,"value":"$HELD"}""";
    private const string RegisterTemplate = """{"step":$STEP,"first":$FIRST,"currentAggregate":$AGGREGATE,"priorAggregate":$PRIOR}""";
    private const string ProposalTemplate = """{"priority":$PRIORITY,"owner":{"replica":"$OWNER","lane":$ON},"value":$CARRIED}""";
    private const string LaneTemplate = """{"replica":"$WHO","lane":$WHICH}""";

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);

    /// <summary>The host the membership admits for <see cref="First"/>, which wrote every snapshot here.</summary>
    private static HostId FirstHost { get; } = Membership.Member(First);

    private static string FirstHex { get; } = Convert.ToHexStringLower(First.AsSpan());
    private static string SecondHex { get; } = Convert.ToHexStringLower(Second.AsSpan());

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    /// <summary>The membership the records in this suite carry.</summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Replica(3)));

    /// <summary>The chain identity the memberships in this suite carry, in lower-case hexadecimal.</summary>
    private static string ClusterHex { get; } = Convert.ToHexStringLower(Configuration.Cluster.AsSpan());

    /// <summary>The host that wrote every snapshot here, written the way the codec writes a host.</summary>
    private static string HostJson { get; } = MemberJson(First);

    /// <summary>The membership's payload, written the way the record codec writes it.</summary>
    private static string ConfigurationJson { get; } =
        $$"""{"cluster":"{{ClusterHex}}","members":[{{MemberJson(First)}},{{MemberJson(Second)}},{{MemberJson(Replica(3))}}]}""";


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// A state whose every slot is present crosses the codec unchanged, down to the committed record's writer,
    /// the lane the leader is bound to and each register slot's proposal key.
    /// </summary>
    [TestMethod]
    public void AVersionedNodeStateWithEveryFieldPresentRoundTrips()
    {
        QuePaxaVersionedNodeState<string> state = new(
            FirstHost,
            new VersionedValue<string>(new RegisterVersion(4UL), Second, Configuration, "committed"),
            new RegisterVersion(5UL),
            ProposerLane.For(Second),
            Configuration,
            new QuePaxaRecorderState<VersionedValue<string>>(
                Four.Next(),
                Proposal(new ProposalPriority(10), ProposerLane.For(Second), 5UL, Second, "first"),
                Proposal(ProposalPriority.Reserved, ProposerLane.For(First), 5UL, First, "aggregate"),
                Proposal(new ProposalPriority(3), ProposerLane.For(First), 5UL, Second, "prior")));

        QuePaxaVersionedNodeState<string> back = RoundTrip(state);

        Assert.AreEqual(state, back);
        Assert.AreEqual(Second, back.Committed!.Writer);
        Assert.AreEqual(new RegisterVersion(4UL), back.Committed.Version);
        Assert.AreEqual(new RegisterVersion(5UL), back.RecorderVersion);
        Assert.AreEqual(ProposerLane.For(Second), back.ConfiguredLeader);
        Assert.AreEqual(Four.Next(), back.Recorder.Step);
        Assert.AreEqual(ProposalPriority.Reserved, back.Recorder.CurrentAggregate!.Key.Priority);
        Assert.AreEqual(First, back.Recorder.PriorAggregate!.Key.Owner.Replica);
        Assert.AreEqual(Second, back.Recorder.PriorAggregate.Value.Writer);
    }


    /// <summary>
    /// A host that has learned nothing and an instance that is leaderless are states the protocol reaches, so
    /// both slots round-trip as null rather than as an omitted field.
    /// </summary>
    [TestMethod]
    public void AnAbsentRecordAndALeaderlessInstanceRoundTripAsNull()
    {
        QuePaxaVersionedNodeState<string> bootstrap = new(
            FirstHost,
            null,
            RegisterVersion.First,
            null,
            Configuration,
            new QuePaxaRecorderState<VersionedValue<string>>(RecorderStep.Zero, null, null, null));

        QuePaxaVersionedNodeState<string> back = RoundTrip(bootstrap);

        Assert.AreEqual(bootstrap, back);
        Assert.IsNull(back.Committed);
        Assert.IsNull(back.ConfiguredLeader);
        Assert.AreEqual(RecorderStep.Zero, back.Recorder.Step);

        //The two nulls are independent, so a host holding a record for a leaderless instance round-trips too.
        QuePaxaVersionedNodeState<string> leaderless = new(
            FirstHost,
            new VersionedValue<string>(new RegisterVersion(4UL), Replica(9), Configuration, "committed"),
            new RegisterVersion(5UL),
            null,
            Configuration,
            new QuePaxaRecorderState<VersionedValue<string>>(RecorderStep.Zero, null, null, null));

        Assert.AreEqual(leaderless, RoundTrip(leaderless));
    }


    /// <summary>
    /// The whole encoding, pinned field by field so that a reordering, a renamed property or a dropped slot is
    /// caught here rather than by a peer that cannot read what this writes.
    /// </summary>
    [TestMethod]
    public void TheEncodingIsPinned()
    {
        QuePaxaVersionedNodeState<string> state = new(
            FirstHost,
            new VersionedValue<string>(new RegisterVersion(4UL), Second, Configuration, "committed"),
            new RegisterVersion(5UL),
            ProposerLane.For(Second),
            Configuration,
            new QuePaxaRecorderState<VersionedValue<string>>(
                Four,
                Proposal(new ProposalPriority(10), ProposerLane.For(Second), 5UL, Second, "v"),
                Proposal(new ProposalPriority(10), ProposerLane.For(Second), 5UL, Second, "v"),
                null));

        string proposal = Proposal("10", SecondHex, "0", Record("5", SecondHex, "v"));
        string expected = NodeState(
            committed: Record("4", SecondHex, "committed"),
            serves: "5",
            leader: Lane(SecondHex, "0"),
            register: Register("4", proposal, proposal, "null"));

        Assert.AreEqual(expected, Serialize(state));
    }


    /// <summary>
    /// There is one encoding of a recorder state and not two: the register nested inside a versioned node state
    /// is byte for byte what the standalone factory writes for the same value. Nothing else in the suite would
    /// notice the two drifting apart.
    /// </summary>
    [TestMethod]
    public void TheNestedRegisterCarriesTheStandaloneEncodingUnchanged()
    {
        QuePaxaRecorderState<VersionedValue<string>> register = new(
            Four,
            Proposal(ProposalPriority.Reserved, ProposerLane.For(Second), 5UL, Second, "v"),
            Proposal(ProposalPriority.Reserved, ProposerLane.For(Second), 5UL, Second, "v"),
            null);

        QuePaxaVersionedNodeState<string> state = new(
            FirstHost,
            new VersionedValue<string>(new RegisterVersion(4UL), Second, Configuration, "committed"),
            new RegisterVersion(5UL),
            ProposerLane.For(Second),
            Configuration,
            register);

        var buffer = new ArrayBufferWriter<byte>();
        QuePaxaMessageJson.CreateRecorderStateSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>(WriteValue))(register, buffer);
        string standalone = Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.EndsWith($$""","recorder":{{standalone}}}""", Serialize(state));
    }


    /// <summary>
    /// A payload omitting any of the six properties is malformed and fails closed, and each omission is refused
    /// by the field's own name, so an omitted slot never decodes as an absent one. The nested register and the
    /// nested membership keep their own labels, so a reader learns which object was short rather than only which
    /// field was.
    /// </summary>
    [TestMethod]
    public void AMissingFieldIsNotAnAbsentSlot()
    {
        string proposal = Proposal("10", SecondHex, "0", Record("5", SecondHex, "v"));
        string register = Register("4", proposal, proposal, "null");
        string committed = Record("4", SecondHex, "committed");
        string leader = Lane(SecondHex, "0");

        (string Json, string Field, string Label)[] vectors =
        [
            (NodeState(committed, "5", leader, register, omitHost: true), "host", "A versioned node state"),
            ($$"""{"host":{{HostJson}},"recorderVersion":5,"configuredLeader":{{leader}},"activeConfiguration":{{ConfigurationJson}},"recorder":{{register}}}""", "committed", "A versioned node state"),
            ($$"""{"host":{{HostJson}},"committed":{{committed}},"configuredLeader":{{leader}},"activeConfiguration":{{ConfigurationJson}},"recorder":{{register}}}""", "recorderVersion", "A versioned node state"),
            ($$"""{"host":{{HostJson}},"committed":{{committed}},"recorderVersion":5,"activeConfiguration":{{ConfigurationJson}},"recorder":{{register}}}""", "configuredLeader", "A versioned node state"),
            (NodeState(committed, "5", leader, register, omitActive: true), "activeConfiguration", "A versioned node state"),
            ($$"""{"host":{{HostJson}},"committed":{{committed}},"recorderVersion":5,"configuredLeader":{{leader}},"activeConfiguration":{{ConfigurationJson}}}""", "recorder", "A versioned node state"),
            (NodeState(committed, "5", leader, register, active: $$"""{"members":[{{MemberJson(First)}}]}"""), "cluster", "A configuration"),
            (NodeState(committed, "5", leader, register, active: $$"""{"cluster":"{{ClusterHex}}"}"""), "members", "A configuration"),
            (NodeState(committed, "5", leader, Register("4", proposal, proposal, omitPrior: true)), "priorAggregate", "A recorder state"),
            (NodeState(Record("4", SecondHex, "committed", omitWriter: true), "5", leader, register), "writer", "A versioned value"),
            (NodeState(Record("4", SecondHex, "committed", omitConfiguration: true), "5", leader, register), "configuration", "A versioned value"),
            (NodeState(committed, "5", leader, register, active: $$"""{"cluster":"{{ClusterHex}}","members":[{"incarnation":"{{Convert.ToHexStringLower(FirstHost.Incarnation.AsSpan())}}"}]}"""), "replica", "A configuration"),
            (NodeState(committed, "5", leader, register, active: $$"""{"cluster":"{{ClusterHex}}","members":[{"replica":"{{FirstHex}}"}]}"""), "incarnation", "A configuration"),

            //A member written as a bare identity is the shape this codec used to carry, and it is malformed
            //input rather than a wrong-kind access: a member names a replica and the store admitted for it,
            //and a payload that states only the first has left the second out.
            (NodeState(committed, "5", leader, register, active: $$"""{"cluster":"{{ClusterHex}}","members":["{{FirstHex}}"]}"""), "replica", "A configuration")
        ];

        foreach((string json, string field, string label) in vectors)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"payload omitting {field}, refused as {label}"));

            MessageDeserializationException failure = Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json));

            Assert.IsInstanceOfType<JsonException>(failure.InnerException);
            Assert.Contains(field, failure.InnerException!.Message);
            Assert.Contains(label, failure.InnerException.Message);
        }
    }


    /// <summary>
    /// The decoder carries no rule that reads two fields at once, so a payload whose stored leader or stored
    /// version disagrees with the committed record beside it decodes into a state and
    /// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> is what refuses the very same value. Both halves are
    /// asserted, because a decoder that refused these would break the split from the accepting side and no
    /// rejection test could tell.
    /// </summary>
    [TestMethod]
    public void TheDecoderAcceptsASnapshotTheRestoreRefuses()
    {
        string proposal = Proposal("10", SecondHex, "0", Record("5", SecondHex, "v"));
        string register = Register("4", proposal, proposal, "null");
        string committed = Record("4", SecondHex, "committed");

        //The stored leader is the configured order's head rather than the record's writer, which is what a host
        //falling back on configuration writes down.
        QuePaxaVersionedNodeState<string> wrongLeader = Deserialize(NodeState(committed, "5", Lane(FirstHex, "0"), register));

        Assert.AreEqual(ProposerLane.For(First), wrongLeader.ConfiguredLeader);
        Assert.AreEqual(Second, wrongLeader.Committed!.Writer);

        StateRestoreException refusedLeader = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, wrongLeader));

        Assert.AreEqual(StateRestoreRefusal.HostLeaderMismatch, refusedLeader.Refusal);
        Assert.AreEqual("state", refusedLeader.ParamName);

        //The stored version names an instance the record does not imply, which is a snapshot torn between two
        //writes.
        QuePaxaVersionedNodeState<string> wrongVersion = Deserialize(NodeState(committed, "9", Lane(SecondHex, "0"), register));

        Assert.AreEqual(new RegisterVersion(9UL), wrongVersion.RecorderVersion);

        StateRestoreException refusedVersion = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, wrongVersion));

        Assert.AreEqual(StateRestoreRefusal.HostRecorderVersionMismatch, refusedVersion.Refusal);
        Assert.AreEqual("state", refusedVersion.ParamName);

        //A register standing at step zero with a proposal in it is a legal payload and an illegal snapshot, and
        //it is the only vector that holds the decoder's prohibition shut for that rule. A rule the decoder must
        //not have is absent code, so no weakening of the decoder can reach it and only a payload an added check
        //would refuse can catch one being added.
        QuePaxaVersionedNodeState<string> unwrittenCarrying = Deserialize(
            NodeState(committed, "5", Lane(SecondHex, "0"), Register("0", proposal, "null", "null")));

        Assert.AreEqual(RecorderStep.Zero, unwrittenCarrying.Recorder.Step);
        Assert.IsNotNull(unwrittenCarrying.Recorder.First);

        StateRestoreException refusedUnwritten = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, unwrittenCarrying));

        Assert.AreEqual(StateRestoreRefusal.HostUnwrittenRecorderCarriesProposal, refusedUnwritten.Refusal);
        Assert.AreEqual("state", refusedUnwritten.ParamName);

        //The stored membership names a set the record does not imply, which is the same tear one field along.
        //The payload is well formed and the decoder builds the configuration from it without complaint.
        string extraMember = $$"""{"cluster":"{{ClusterHex}}","members":[{{MemberJson(First)}},{{MemberJson(Second)}},{{MemberJson(Replica(3))}},{{MemberJson(Replica(4))}}]}""";
        QuePaxaVersionedNodeState<string> wrongMembership = Deserialize(NodeState(committed, "5", Lane(SecondHex, "0"), register, active: extraMember));

        Assert.HasCount(4, wrongMembership.ActiveConfiguration.Members);
        Assert.AreEqual(Configuration.Cluster, wrongMembership.ActiveConfiguration.Cluster);

        StateRestoreException refusedMembership = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, wrongMembership));

        Assert.AreEqual(StateRestoreRefusal.HostConfigurationMismatch, refusedMembership.Refusal);
        Assert.AreEqual("state", refusedMembership.ParamName);
    }


    /// <summary>
    /// A value only its own type can be wrong about is the codec's business. The unwritten version, a version
    /// above the range, a negative lane on the configured leader and an identity of the wrong width each fail
    /// closed with the domain validator's own exception reaching the reader as the inner cause.
    /// </summary>
    [TestMethod]
    public void ASnapshotCarryingASingleIllegalValueFailsClosed()
    {
        string proposal = Proposal("10", SecondHex, "0", Record("5", SecondHex, "v"));
        string register = Register("4", proposal, proposal, "null");
        string committed = Record("4", SecondHex, "committed");
        string leader = Lane(SecondHex, "0");

        //No host serves the unwritten version, so the record refuses it naming its own property.
        Exception unwritten = ValidatorFailure(NodeState(committed, "0", leader, register));

        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(unwritten);
        Assert.AreEqual("RecorderVersion", ((ArgumentOutOfRangeException)unwritten).ParamName);

        //One above the range, written as a literal rather than computed from the type's own bound, so that
        //raising the bound does not move the vector with the mutant it exists to catch.
        Exception aboveRange = ValidatorFailure(NodeState(committed, "9007199254740992", leader, register));

        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(aboveRange);
        Assert.AreEqual("Value", ((ArgumentOutOfRangeException)aboveRange).ParamName);

        //A negative lane, which ProposerLane refuses naming its own lane.
        Exception negativeLane = ValidatorFailure(NodeState(committed, "5", Lane(SecondHex, "-1"), register));

        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(negativeLane);
        Assert.AreEqual("Lane", ((ArgumentOutOfRangeException)negativeLane).ParamName);

        //Sixty-two characters are thirty-one bytes, which decode cleanly and then reach ReplicaId's width
        //guard, so the vector tests the width and not the hex.
        Exception wrongWidth = ValidatorFailure(NodeState(Record("4", new string('a', 62), "committed"), "5", leader, register));

        Assert.IsInstanceOfType<ArgumentException>(wrongWidth);
        Assert.Contains("ReplicaId requires exactly", wrongWidth.Message);
    }


    /// <summary>Builds a proposal carrying a decided record, which is what a versioned register's slots hold.</summary>
    /// <param name="priority">The proposal's priority.</param>
    /// <param name="owner">The lane that owns the proposal.</param>
    /// <param name="version">The version the carried record is written at.</param>
    /// <param name="writer">The replica the carried record is written by.</param>
    /// <param name="value">The application value.</param>
    /// <returns>The proposal.</returns>
    private static PrioritizedProposal<VersionedValue<string>> Proposal(ProposalPriority priority, ProposerLane owner, ulong version, ReplicaId writer, string value)
    {
        return new PrioritizedProposal<VersionedValue<string>>(
            new ProposalKey(priority, owner),
            new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, value));
    }


    /// <summary>Builds a versioned node state's payload, optionally omitting its membership slot.</summary>
    /// <param name="committed">The committed record's payload, or a null literal.</param>
    /// <param name="serves">The version the recorder serves.</param>
    /// <param name="leader">The configured leader's payload, or a null literal.</param>
    /// <param name="register">The recorder's payload.</param>
    /// <param name="active">The membership's payload, or <see langword="null"/> for the one the records carry.</param>
    /// <param name="omitActive">Whether to omit the membership field, which makes the state malformed.</param>
    /// <returns>The payload.</returns>
    private static string NodeState(string committed, string serves, string leader, string register, string? active = null, bool omitActive = false, bool omitHost = false)
    {
        if(omitActive)
        {
            return $$"""{"host":{{HostJson}},"committed":{{committed}},"recorderVersion":{{serves}},"configuredLeader":{{leader}},"recorder":{{register}}}""";
        }

        if(omitHost)
        {
            return $$"""{"committed":{{committed}},"recorderVersion":{{serves}},"configuredLeader":{{leader}},"activeConfiguration":{{active ?? ConfigurationJson}},"recorder":{{register}}}""";
        }

        return NodeStateTemplate
            .Replace("$WHOSE", HostJson, StringComparison.Ordinal)
            .Replace("$COMMITTED", committed, StringComparison.Ordinal)
            .Replace("$SERVES", serves, StringComparison.Ordinal)
            .Replace("$LEADER", leader, StringComparison.Ordinal)
            .Replace("$ACTIVE", active ?? ConfigurationJson, StringComparison.Ordinal)
            .Replace("$REGISTER", register, StringComparison.Ordinal);
    }


    /// <summary>Builds a committed record's payload, optionally omitting one of its required slots.</summary>
    /// <param name="at">The version.</param>
    /// <param name="by">The writer's identity in lower-case hexadecimal.</param>
    /// <param name="held">The application value.</param>
    /// <param name="omitWriter">Whether to omit the writer field, which makes the record malformed.</param>
    /// <param name="omitConfiguration">Whether to omit the configuration field, which makes the record malformed.</param>
    /// <returns>The payload.</returns>
    private static string Record(string at, string by, string held, bool omitWriter = false, bool omitConfiguration = false)
    {
        if(omitWriter)
        {
            return $$"""{"version":{{at}},"configuration":{{ConfigurationJson}},"value":"{{held}}"}""";
        }

        if(omitConfiguration)
        {
            return $$"""{"version":{{at}},"writer":"{{by}}","value":"{{held}}"}""";
        }

        return RecordTemplate
            .Replace("$AT", at, StringComparison.Ordinal)
            .Replace("$BY", by, StringComparison.Ordinal)
            .Replace("$UNDER", ConfigurationJson, StringComparison.Ordinal)
            .Replace("$HELD", held, StringComparison.Ordinal);
    }


    /// <summary>Builds a register's payload, optionally omitting its prior aggregate.</summary>
    /// <param name="step">The step.</param>
    /// <param name="first">The first proposal's payload, or a null literal.</param>
    /// <param name="aggregate">The current aggregate's payload, or a null literal.</param>
    /// <param name="prior">The prior aggregate's payload, or a null literal.</param>
    /// <param name="omitPrior">Whether to omit the prior aggregate field, which makes the register malformed.</param>
    /// <returns>The payload.</returns>
    private static string Register(string step, string first, string aggregate, string? prior = null, bool omitPrior = false)
    {
        if(omitPrior)
        {
            return $$"""{"step":{{step}},"first":{{first}},"currentAggregate":{{aggregate}}}""";
        }

        return RegisterTemplate
            .Replace("$STEP", step, StringComparison.Ordinal)
            .Replace("$FIRST", first, StringComparison.Ordinal)
            .Replace("$AGGREGATE", aggregate, StringComparison.Ordinal)
            .Replace("$PRIOR", prior!, StringComparison.Ordinal);
    }


    private static string Proposal(string priority, string owner, string on, string carried)
    {
        return ProposalTemplate
            .Replace("$PRIORITY", priority, StringComparison.Ordinal)
            .Replace("$OWNER", owner, StringComparison.Ordinal)
            .Replace("$ON", on, StringComparison.Ordinal)
            .Replace("$CARRIED", carried, StringComparison.Ordinal);
    }


    /// <summary>
    /// One member's payload, written the way the configuration codec writes a host: the replica it serves
    /// under beside the store admitted to answer for it.
    /// </summary>
    /// <param name="replica">The replica the member is listed under.</param>
    /// <returns>The member's payload.</returns>
    private static string MemberJson(ReplicaId replica)
    {
        return $$"""{"replica":"{{Convert.ToHexStringLower(replica.AsSpan())}}","incarnation":"{{Convert.ToHexStringLower(Membership.Member(replica).Incarnation.AsSpan())}}"}""";
    }


    private static string Lane(string who, string which)
    {
        return LaneTemplate
            .Replace("$WHO", who, StringComparison.Ordinal)
            .Replace("$WHICH", which, StringComparison.Ordinal);
    }


    /// <summary>
    /// Returns the domain validator's own exception from inside a rejected payload, after asserting the two
    /// wrappers around it: the guard's uniform failure and the codec's JSON fault.
    /// </summary>
    /// <param name="json">The payload.</param>
    /// <returns>The innermost exception, which is what the value's own type threw.</returns>
    private static Exception ValidatorFailure(string json)
    {
        MessageDeserializationException failure = Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json));

        Assert.IsInstanceOfType<JsonException>(failure.InnerException);
        Assert.Contains("a QuePaxa message rejects", failure.InnerException!.Message);
        Assert.IsNotNull(failure.InnerException.InnerException);

        return failure.InnerException.InnerException!;
    }


    private static QuePaxaVersionedNodeState<string> RoundTrip(QuePaxaVersionedNodeState<string> state)
    {
        return Deserialize(Serialize(state));
    }


    private static string Serialize(QuePaxaVersionedNodeState<string> state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        QuePaxaMessageJson.CreateVersionedNodeStateSerializer<string>(WriteValue)(state, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static QuePaxaVersionedNodeState<string> Deserialize(string json)
    {
        return QuePaxaMessageJson.CreateVersionedNodeStateDeserializer(ReadValue)(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static void WriteValue(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadValue(JsonElement element) => element.GetString()!;


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
