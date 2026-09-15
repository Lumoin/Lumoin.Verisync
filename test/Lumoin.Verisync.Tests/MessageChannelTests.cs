using Lumoin.Verisync.Core;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;

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
        Assert.AreSequenceEqual(expected, received.ToArray());
    }


    [TestMethod]
    public async Task EmptyChannelYieldsNoMessages()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.IsEmpty(received);
    }


    [TestMethod]
    public async Task ACancelBeforeTheFirstReadEndsTheEnumerationAndDiscardsBufferedFrames()
    {
        //Two frames are on the pipe and no read is in flight, so the cancel takes the next read: the first
        //MoveNextAsync completes false and neither buffered frame is delivered. A consumer that wants the
        //buffered tail does not cancel.
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("beta", TestContext.CancellationToken).ConfigureAwait(false);

        reader.CancelPendingRead();

        IAsyncEnumerator<string> messages = reader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);

        Assert.IsFalse(
            await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false),
            "A canceled read delivered a buffered frame instead of ending the enumeration.");

        await messages.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACancelWithFramesInHandStillYieldsThemAndThenEndsTheEnumeration()
    {
        //Three frames are on the pipe before the enumerator exists, so one read takes them all into hand. A
        //cancel is consulted where the next read is issued, not between the frames of the buffer already read,
        //so the frames in hand are yielded first and the enumeration ends only at that next read. A consumer
        //that must observe nothing further stops enumerating rather than relying on the cancel.
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("beta", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("gamma", TestContext.CancellationToken).ConfigureAwait(false);

        IAsyncEnumerator<string> messages = reader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);

        Assert.IsTrue(await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual("alpha", messages.Current);

        reader.CancelPendingRead();

        Assert.IsTrue(
            await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false),
            "The cancel dropped a frame the read had already taken into hand.");
        Assert.AreEqual("beta", messages.Current);

        Assert.IsTrue(
            await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false),
            "The cancel dropped a frame the read had already taken into hand.");
        Assert.AreEqual("gamma", messages.Current);

        Assert.IsFalse(
            await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false),
            "The buffer in hand was exhausted, so the next read had to be the canceled one that ends the enumeration.");

        await messages.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACancelAfterTheEnumerationEndedIsToleratedAndDisposesCleanly()
    {
        //A teardown cancels every reader it holds without asking whether that reader's stream already ended,
        //so a cancel on a finished enumeration must be a no-op rather than a fault.
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        IAsyncEnumerator<string> messages = reader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);

        Assert.IsTrue(await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual("alpha", messages.Current);
        Assert.IsFalse(await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false));

        reader.CancelPendingRead();

        await messages.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
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

        Assert.AreSequenceEqual(messages, received.ToArray());
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


    /// <summary>
    /// A padded frame whose declared outer length holds only the four-byte inner length prefix, with a real
    /// length of zero and no payload bytes, is a valid empty-payload frame, not one too short for the prefix.
    /// </summary>
    [TestMethod]
    public async Task APaddedFrameExactlyFillingTheInnerLengthPrefixIsAccepted()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, padding: padding);

        Memory<byte> frame = pipe.Writer.GetMemory(8)[..8];
        frame.Span.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.Span, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.Span[4..], 0);
        pipe.Writer.Advance(8);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreEqual("", received[0]);
    }


    /// <summary>
    /// Pins that a doubling policy rounds a requirement that lands on an exact but non-power-of-two multiple
    /// of the minimum bucket up to the next power of two, rather than to that exact multiple.
    /// </summary>
    [TestMethod]
    public void PowersOfTwoRoundsUpToTheNextPowerOfTwoNotTheExactMultiple()
    {
        FramePadding powers = FramePadding.PowersOfTwo(64);

        //A 188-byte payload needs 192 bytes with the inner prefix: exactly three minimum buckets. The doubling
        //ladder must still round that up to the next power of two, four buckets (256), not stop at three (192).
        Assert.AreEqual(256, powers.PaddedLength(188));
    }


    /// <summary>
    /// Pins that a fixed-step policy maps a requirement landing exactly on a bucket multiple to that same
    /// multiple, not the next one up.
    /// </summary>
    [TestMethod]
    public void FixedBucketsMapsAnExactMultipleToItselfNotTheNextOne()
    {
        FramePadding fixedBuckets = FramePadding.FixedBuckets(100);

        //A 196-byte payload needs 200 bytes with the inner prefix: exactly two buckets, landing precisely on
        //the multiple boundary the ceiling-division arithmetic must not overshoot.
        Assert.AreEqual(200, fixedBuckets.PaddedLength(196));
    }


    /// <summary>
    /// Pins that a serialized payload exactly at the configured maximum frame length is written and round-trips,
    /// not rejected: the guard rejects payloads strictly above the maximum, not payloads at it.
    /// </summary>
    [TestMethod]
    public async Task PayloadExactlyAtTheConfiguredMaximumIsAccepted()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, maxFrameLength: 5);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, maxFrameLength: 5);
        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreEqual("alpha", received[0]);
    }


    /// <summary>
    /// Pins that a fixed-step policy rounds a requirement up to the exact multiple of the bucket size, never
    /// to the next power of two a doubling policy would use.
    /// </summary>
    [TestMethod]
    public void FixedBucketsUsesTheExactMultipleNotTheNextPowerOfTwo()
    {
        FramePadding fixedBuckets = FramePadding.FixedBuckets(100);

        //A 296-byte payload needs 300 bytes with the inner prefix: exactly three buckets. A doubling policy
        //would round that up to four buckets (400) instead of stopping at the exact multiple (300).
        Assert.AreEqual(300, fixedBuckets.PaddedLength(296));
    }


    /// <summary>
    /// A frame declaring exactly the configured maximum payload length is accepted; the maximum is the
    /// largest payload accepted, not an exclusive bound.
    /// </summary>
    [TestMethod]
    public async Task AFrameExactlyAtTheConfiguredMaximumIsAccepted()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, maxFrameLength: 8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, maxFrameLength: 8);

        await writer.WriteAsync("12345678", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreEqual("12345678", received[0]);
    }


    /// <summary>
    /// A lone zero-length payload frame completes from exactly its four buffered header bytes; the reader
    /// must not wait for further bytes that are never coming.
    /// </summary>
    [TestMethod]
    public async Task AZeroLengthPayloadFrameCompletesFromExactlyItsHeaderBytes()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        //The frame is complete from its four header bytes alone. A reader that demanded a further byte before
        //accepting the completed pipe would instead see the pipe completed with four bytes unconsumed and throw,
        //so draining to exactly one empty message pins the boundary as an assertion rather than a deadline.
        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreEqual("", received[0]);
    }


    /// <summary>
    /// A padded frame shorter than the four-byte inner length prefix fails closed instead of reading out of
    /// bounds.
    /// </summary>
    [TestMethod]
    public async Task APaddedFrameShorterThanItsInnerLengthPrefixFailsClosed()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);

        Pipe pipe = new();
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8, padding: padding);

        Memory<byte> frame = pipe.Writer.GetMemory(6)[..6];
        frame.Span.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.Span, 2);
        pipe.Writer.Advance(6);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAll(reader)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that a padded frame above the configured maximum throws InvalidOperationException: the writer fails
    /// locally rather than emitting a bucket the reading peer's limit would reject.
    /// </summary>
    [TestMethod]
    public async Task PaddedFrameAboveTheConfiguredMaximumIsRejected()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, maxFrameLength: 63, padding: padding);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await writer.WriteAsync("", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that a padded frame exactly at the configured maximum is written, not rejected: the guard rejects
    /// padded frames strictly above the maximum, not padded frames at it.
    /// </summary>
    [TestMethod]
    public async Task PaddedFrameExactlyAtTheConfiguredMaximumIsAccepted()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, maxFrameLength: 64, padding: padding);

        await writer.WriteAsync("", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<int> outerLengths = await ReadRawOuterFrameLengths(pipe.Reader).ConfigureAwait(false);

        Assert.HasCount(1, outerLengths);
        Assert.AreEqual(64, outerLengths[0]);
    }


    /// <summary>
    /// A read that has to park (no frame buffered yet) resumes without posting back to the
    /// synchronization context that was current when the read was awaited, so a host that blocks its own
    /// thread on the enumeration cannot deadlock the loop.
    /// </summary>
    [TestMethod]
    public async Task APendingReadResumesOffTheCallersSynchronizationContext()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);
        PostCountingSynchronizationContext context = new();

        IAsyncEnumerator<string> messages = reader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);

        Task<bool> moveNext = context.Start(() => messages.MoveNextAsync().AsTask());

        Assert.IsFalse(moveNext.IsCompleted, "No frame was buffered yet, so the read should have parked instead of completing at once.");

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(
            await moveNext.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual("alpha", messages.Current);
        Assert.AreEqual(0, context.Posts, "The resumed read posted its continuation back to the caller's synchronization context instead of completing off it.");

        await writer.CompleteAsync().ConfigureAwait(false);
        await messages.DisposeAsync().AsTask().WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that a flush parked on the pipe's pause threshold resumes off the thread pool instead of the
    /// caller's captured context, matching the writer's ConfigureAwait(false) on the pipe flush, so a host that
    /// blocks its own thread on a back-pressured write cannot deadlock it.
    /// </summary>
    [TestMethod]
    public async Task APendingFlushResumesOffTheCallersSynchronizationContext()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 16, resumeWriterThreshold: 8));
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        PostCountingSynchronizationContext context = new();

        Task write = context.Start(() => writer.WriteAsync(new string('x', 32), TestContext.CancellationToken).AsTask());

        //The thirty-six-byte frame is above the sixteen-byte pause threshold, so the flush parked instead of completing at once.
        Assert.IsFalse(write.IsCompleted, "The flush completed synchronously, so a captured continuation could not be observed.");

        ReadResult result = await pipe.Reader.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
        pipe.Reader.AdvanceTo(result.Buffer.End);

        await write.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, context.Posts, "The resumed flush posted its continuation back to the caller's synchronization context instead of completing off it.");

        await pipe.Reader.CompleteAsync().ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that the padding fill region is committed to the pipe, not merely written into an unadvanced span:
    /// the total raw bytes on the wire for a padded frame equal the outer prefix plus the full padded length.
    /// </summary>
    [TestMethod]
    public async Task PaddedFrameFillBytesAreCommittedToTheWire()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8, padding: padding);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        var raw = new ArrayBufferWriter<byte>();
        while(true)
        {
            ReadResult result = await pipe.Reader.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
            foreach(ReadOnlyMemory<byte> segment in result.Buffer)
            {
                raw.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(result.Buffer.End);
            if(result.IsCompleted)
            {
                break;
            }
        }

        await pipe.Reader.CompleteAsync().ConfigureAwait(false);

        int outerLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.WrittenSpan[..4]);
        int expectedPadded = padding.PaddedLength(Encoding.UTF8.GetByteCount("alpha"));

        Assert.AreEqual(expectedPadded, outerLength);
        Assert.AreEqual(4 + expectedPadded, raw.WrittenCount);
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


    /// <summary>
    /// Pins that the padding fill is zeroed, not merely advanced over: a writer that hands back memory
    /// pre-soiled with a non-zero pattern still ships an all-zero fill, so no uninitialized byte leaks into the
    /// padding on the wire. A fresh pipe returns zeroed memory, so only a dirty buffer exposes the zeroing.
    /// </summary>
    [TestMethod]
    public async Task PaddedFrameFillBytesAreZeroedEvenWhenTheUnderlyingBufferIsDirty()
    {
        FramePadding padding = FramePadding.PowersOfTwo(64);
        DirtyingPipeWriter dirty = new();
        MessageChannelWriter<string> writer = new(dirty, SerializeUtf8, padding: padding);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        int payloadLength = Encoding.UTF8.GetByteCount("alpha");
        int paddedLength = padding.PaddedLength(payloadLength);

        //The frame is the outer four-byte prefix, the inner four-byte prefix, the payload, then zero fill to the bucket boundary.
        const int headerLength = 4;
        int fillStart = headerLength + headerLength + payloadLength;
        int frameEnd = headerLength + paddedLength;

        ReadOnlySpan<byte> raw = dirty.WrittenSpan;
        int rawLength = raw.Length;
        Assert.AreEqual(frameEnd, rawLength);
        Assert.IsGreaterThan(fillStart, frameEnd, "The chosen payload leaves no fill region to check.");
        Assert.AreEqual(-1, raw[fillStart..frameEnd].IndexOfAnyExcept((byte)0), "A padding fill byte was left non-zero.");
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


    /// <summary>
    /// A <see cref="PipeWriter"/> that accumulates into a single growing buffer and hands back every exposed
    /// region pre-filled with <c>0xFF</c>, so any span the writer takes but does not explicitly zero is
    /// observably dirty once committed. The committed bytes are exposed through <see cref="WrittenSpan"/>.
    /// </summary>
    private sealed class DirtyingPipeWriter: PipeWriter
    {
        private byte[] buffer = new byte[256];

        private int committed;

        /// <summary>The bytes committed so far.</summary>
        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, committed);

        /// <inheritdoc/>
        public override void Advance(int bytes) => committed += bytes;

        /// <inheritdoc/>
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureAndDirty(sizeHint);

            return buffer.AsMemory(committed);
        }

        /// <inheritdoc/>
        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureAndDirty(sizeHint);

            return buffer.AsSpan(committed);
        }

        /// <inheritdoc/>
        public override void CancelPendingFlush()
        {
        }

        /// <inheritdoc/>
        public override void Complete(Exception? exception = null)
        {
        }

        /// <inheritdoc/>
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            new(new FlushResult(isCanceled: false, isCompleted: false));

        /// <summary>Grows the buffer to hold <paramref name="sizeHint"/> more bytes and fills every uncommitted byte with <c>0xFF</c>.</summary>
        private void EnsureAndDirty(int sizeHint)
        {
            int required = committed + Math.Max(sizeHint, 1);
            if(required > buffer.Length)
            {
                Array.Resize(ref buffer, Math.Max(required, buffer.Length * 2));
            }

            buffer.AsSpan(committed).Fill(0xFF);
        }
    }
}
