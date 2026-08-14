using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Runs a whole QuePaxa proposal with the JSON codec in the loop, so a proposal that reaches a recorder comes
/// back as a DIFFERENT INSTANCE.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONLY THING IN THE SYSTEM THAT CAN DETECT A REFERENCE-EQUALITY VALUE TYPE. The fast path and the
/// phase-two decision compare whole proposals, and the synthesized record equality routes the value through
/// <see cref="EqualityComparer{T}.Default"/>. Every other QuePaxa test passes the same object from proposer to
/// recorder and back, so under reference equality the object still equals itself, the comparison holds, and
/// the decision fires. A codec is what makes the recorder's copy distinct, and the defect it exposes is a
/// SILENT LIVENESS FAILURE rather than an exception: nothing throws, the round trip is byte-perfect, and the
/// proposer simply never decides.
/// </para>
/// <para>
/// THE NEGATIVE CASE PINS ITS STEP COUNT AND NOT ONLY ITS UNDECIDEDNESS, because every failure mode of this
/// harness leaves a proposal undecided. A codec seam that threw on every message would return undecided after
/// a single step, and since the negative run uses its own value type and therefore its own read and write
/// callbacks, no assertion otherwise links it to the positive run. The step count is what says the run
/// actually reached every recorder and could not match, rather than never having reached one.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaCodecDecisionTests
{
    /// <summary>
    /// Steps 4 through the last representable step, which is what an undecided run exhausts.
    /// </summary>
    private const int StepsToExhaustion = 1024;

    private static TimeSpan ProposalTimeout { get; } = TimeSpan.FromSeconds(30);


    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task AProposalDecidesThroughPhaseTwoAcrossTheCodec()
    {
        SeededPrioritySource source = new(20260808);
        var proposer = new QuePaxaProposer<CodecValue>(
            EndpointsOver(CodecValueCodec.Write, CodecValueCodec.Read, recorderCount: 3),
            ProposerLane.For(Replica(1)),
            source.Next,
            attemptsPerRecorder: 3);

        var value = new CodecValue("the decided value");

        QuePaxaOutcome<CodecValue> outcome = await AwaitProposalAsync(
            proposer.ProposeAsync(believedLeader: null, value, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);

        //Step six is round one phase two, which proves the decision came through the phase-two comparison
        //rather than through the reserved fast path at step four. A proposer that claims no leadership starts
        //from the absent priority and redraws an ordinary one, so the fast path cannot fire at all.
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), outcome.DecidedAt);
        Assert.AreEqual(value, outcome.Value);
        Assert.AreEqual(3, outcome.Steps);
    }


    [TestMethod]
    public async Task AReferenceEqualityValueNeverDecidesAndExhaustsTheStepBudget()
    {
        //The source must be unbounded here. An undecided run redraws once per recorder at every phase-zero
        //step, which is far past the end of any scripted sequence.
        SeededPrioritySource source = new(20260808);
        var proposer = new QuePaxaProposer<ReferenceOnlyValue>(
            EndpointsOver(ReferenceOnlyCodec.Write, ReferenceOnlyCodec.Read, recorderCount: 3),
            ProposerLane.For(Replica(1)),
            source.Next,
            attemptsPerRecorder: 3);

        QuePaxaOutcome<ReferenceOnlyValue> outcome = await AwaitProposalAsync(
            proposer.ProposeAsync(believedLeader: null, new ReferenceOnlyValue("never decided"), TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsFalse(outcome.IsDecided);
        Assert.AreEqual(StepsToExhaustion, outcome.Steps);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"steps={outcome.Steps}, draws={source.DrawCount}"));
    }


    private static RecorderEndpointDelegate<TValue>[] EndpointsOver<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue, ReadValueDelegate<JsonElement, TValue> readValue, int recorderCount)
    {
        SerializeMessageDelegate<RecordRequest<TValue>> writeRequest = QuePaxaMessageJson.CreateRequestSerializer(writeValue);
        DeserializeMessageDelegate<RecordRequest<TValue>> readRequest = QuePaxaMessageJson.CreateRequestDeserializer(readValue);
        SerializeMessageDelegate<RecordReply<TValue>> writeReply = QuePaxaMessageJson.CreateReplySerializer(writeValue);
        DeserializeMessageDelegate<RecordReply<TValue>> readReply = QuePaxaMessageJson.CreateReplyDeserializer(readValue);

        var endpoints = new RecorderEndpointDelegate<TValue>[recorderCount];
        for(int i = 0; i < recorderCount; i++)
        {
            var node = new QuePaxaNode<TValue>(QuePaxaRecorder<TValue>.Leaderless);

            endpoints[i] = (request, _) =>
            {
                //Both directions cross the codec, so neither the recorder's copy of the proposal nor the
                //proposer's copy of the answer is the instance the other side holds.
                RecordReply<TValue> reply = node.Handle(Roundtrip(request, writeRequest, readRequest));

                return ValueTask.FromResult(Roundtrip(reply, writeReply, readReply));
            };
        }

        return endpoints;
    }


    private static TMessage Roundtrip<TMessage>(TMessage message, SerializeMessageDelegate<TMessage> serialize, DeserializeMessageDelegate<TMessage> deserialize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serialize(message, buffer);

        return deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static async Task<QuePaxaOutcome<TValue>> AwaitProposalAsync<TValue>(Task<QuePaxaOutcome<TValue>> proposal)
    {
        return await proposal.WaitAsync(ProposalTimeout).ConfigureAwait(false);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// A record, so the synthesized value equality is what the phase-two comparison sees.
    /// </summary>
    private sealed record CodecValue(string Text);


    /// <summary>
    /// A plain sealed class with no equality of its own, so <c>EqualityComparer&lt;T&gt;.Default</c> falls back
    /// to reference equality.
    /// </summary>
    /// <remarks>
    /// This is the shape a caller can supply today without any compiler complaint, and the shape the whole
    /// slice exists to make detectable.
    /// </remarks>
    private sealed class ReferenceOnlyValue(string text)
    {
        public string Text { get; } = text;
    }


    private static class CodecValueCodec
    {
        public static void Write(Utf8JsonWriter writer, CodecValue value) => writer.WriteStringValue(value.Text);


        public static CodecValue Read(JsonElement element) => new(element.GetString()!);
    }


    private static class ReferenceOnlyCodec
    {
        public static void Write(Utf8JsonWriter writer, ReferenceOnlyValue value) => writer.WriteStringValue(value.Text);


        public static ReferenceOnlyValue Read(JsonElement element) => new(element.GetString()!);
    }
}
