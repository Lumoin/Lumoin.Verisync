using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class MessageChannelTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SerializeMessageDelegate<string> SerializeUtf8 { get; } =
        (message, output) => output.Write(Encoding.UTF8.GetBytes(message));

    private static DeserializeMessageDelegate<string> DeserializeUtf8 { get; } =
        payload => Encoding.UTF8.GetString(payload.ToArray());


    [TestMethod]
    public async Task RoundTripsFramedMessagesOverInMemoryPipe()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("a longer message with spaces", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        string[] expected = ["alpha", "", "a longer message with spaces"];
        CollectionAssert.AreEqual(expected, received.ToArray());
    }


    [TestMethod]
    public async Task EmptyChannelYieldsNoMessages()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(0, received);
    }


    [TestMethod]
    public void ConstructorsRejectNullArguments()
    {
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelWriter<string>(null!, SerializeUtf8));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelWriter<string>(pipe.Writer, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelReader<string>(null!, DeserializeUtf8));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelReader<string>(pipe.Reader, null!));
    }


    [TestMethod]
    public void ConstructorsRejectNonPositiveFrameLimits()
    {
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MessageChannelWriter<string>(pipe.Writer, SerializeUtf8, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MessageChannelReader<string>(pipe.Reader, DeserializeUtf8, 0));
    }


    [TestMethod]
    public async Task HostileLengthPrefixFailsTheChannelInsteadOfBuffering()
    {
        //A peer claiming a ~4 GiB frame with a four-byte header must fail the read immediately; the
        //declared length is attacker-controlled and is never trusted past the configured maximum.
        Pipe pipe = new();
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        Memory<byte> header = pipe.Writer.GetMemory(4)[..4];
        header.Span.Fill(0xFF);
        pipe.Writer.Advance(4);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAll(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task FrameAboveTheConfiguredMaximumIsRejected()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, maxFrameLength: 8);

        await writer.WriteAsync("nine bytes", TestContext.CancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAll(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task WriterRejectsPayloadAboveItsMaximum()
    {
        //Failing locally is friendlier than having a compliant peer kill the connection on receipt.
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, maxFrameLength: 4);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ChannelEndingMidFrameThrows()
    {
        //The header promises ten payload bytes but the writer completes after three: a protocol violation.
        Pipe pipe = new();
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        Memory<byte> frame = pipe.Writer.GetMemory(7)[..7];
        frame.Span.Clear();
        frame.Span[3] = 10;
        pipe.Writer.Advance(7);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAll(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task PaddedFramesRoundTripAndEveryWireLengthIsABucket()
    {
        //Real payload lengths spanning a bucket boundary: 60 + 4 inner prefix exactly fills the 64 bucket.
        string[] messages =
        [
            new string('x', 0),
            new string('x', 1),
            new string('x', 59),
            new string('x', 60),
            new string('x', 61),
            new string('x', 200)
        ];

        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, padding: padding);
        foreach(string message in messages)
        {
            await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);

        //Read the raw bytes off a plain reader so the outer prefixes can be parsed without the channel's help.
        List<int> outerLengths = await ReadRawOuterFrameLengths(pipe.Reader).ConfigureAwait(false);

        foreach(int outerLength in outerLengths)
        {
            Assert.AreEqual(padding.PaddedLength(outerLength - 4), outerLength, $"Outer frame length {outerLength} is not a bucket size.");
        }

        //The same bytes round-trip through a configured reader back to the original messages.
        Pipe roundTrip = new();
        MessageChannelWriter<string> roundTripWriter = new(roundTrip.Writer, SerializeUtf8, padding: padding);
        MessageChannelReader<string> roundTripReader = new(roundTrip.Reader, DeserializeUtf8, padding: padding);
        foreach(string message in messages)
        {
            await roundTripWriter.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
        }

        await roundTripWriter.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(roundTripReader).ConfigureAwait(false);

        CollectionAssert.AreEqual(messages, received.ToArray());
    }


    [TestMethod]
    public async Task PaddedMessagesInTheSameBucketShareAWireLength()
    {
        //Two real lengths that both land in the smallest 64 bucket must be indistinguishable on the wire.
        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, padding: padding);
        await writer.WriteAsync(new string('a', 3), TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(new string('b', 40), TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<int> outerLengths = await ReadRawOuterFrameLengths(pipe.Reader).ConfigureAwait(false);

        Assert.HasCount(2, outerLengths);
        Assert.AreEqual(outerLengths[0], outerLengths[1]);
    }


    [TestMethod]
    public async Task HostileInnerLengthFailsThePaddedChannel()
    {
        //A well-formed 64-byte bucket whose inner length claims to reach past the frame is rejected; the
        //inner prefix is attacker-influenced and is never trusted past the frame bounds.
        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, padding: padding);

        const int bucket = 64;
        Memory<byte> frame = pipe.Writer.GetMemory(4 + bucket)[..(4 + bucket)];
        frame.Span.Clear();

        //Outer prefix: the padded bucket length.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.Span, bucket);

        //Inner prefix: a real length one byte beyond what the 60-byte payload region can hold.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.Span[4..], (uint)(bucket - 4 + 1));

        pipe.Writer.Advance(4 + bucket);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAll(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task PaddedWriterWithUnpaddedReaderFramesButDeliversThePaddedBlob()
    {
        //A configuration mismatch must not crash the framing: the reader frames off the trusted outer prefix
        //and hands the deserializer the whole padded blob, which differs from the original message.
        const string original = "alpha";
        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, padding: padding);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync(original, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreNotEqual(original, received[0]);
        Assert.AreEqual(padding.PaddedLength(Encoding.UTF8.GetByteCount(original)), received[0].Length);
    }


    [TestMethod]
    public void FramePaddingValidatesItsArguments()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FramePadding.PowersOfTwo(4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FramePadding.PowersOfTwo(48));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FramePadding.FixedBuckets(7));

        FramePadding padding = FramePadding.PowersOfTwo(64);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => padding.PaddedLength(-1));
    }


    [TestMethod]
    public void PaddedLengthRoundsToBuckets()
    {
        FramePadding powers = FramePadding.PowersOfTwo(64);
        Assert.AreEqual(64, powers.PaddedLength(0));
        Assert.AreEqual(64, powers.PaddedLength(64 - 4));
        Assert.AreEqual(128, powers.PaddedLength(64 - 3));

        FramePadding fixedBuckets = FramePadding.FixedBuckets(100);
        Assert.AreEqual(100, fixedBuckets.PaddedLength(0));
        Assert.AreEqual(100, fixedBuckets.PaddedLength(100 - 4));
        Assert.AreEqual(200, fixedBuckets.PaddedLength(100 - 3));
    }


    private async Task<List<int>> ReadRawOuterFrameLengths(PipeReader reader)
    {
        var bytes = new ArrayBufferWriter<byte>();
        while(true)
        {
            ReadResult result = await reader.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
            foreach(ReadOnlyMemory<byte> segment in result.Buffer)
            {
                bytes.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if(result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);

        var lengths = new List<int>();
        ReadOnlySpan<byte> all = bytes.WrittenSpan;
        int offset = 0;
        while(offset < all.Length)
        {
            int outerLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(all.Slice(offset, 4));
            lengths.Add(outerLength);
            offset += 4 + outerLength;
        }

        return lengths;
    }


    private async Task<List<string>> ReadAll(MessageChannelReader<string> reader)
    {
        var received = new List<string>();
        await foreach(string message in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            received.Add(message);
        }

        return received;
    }
}
