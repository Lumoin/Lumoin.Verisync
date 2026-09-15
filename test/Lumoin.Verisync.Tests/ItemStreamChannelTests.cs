using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Tests for <see cref="ItemStreamChannelReader{TItem}"/> — the length-prefixed item-stream reader that drives
/// a per-item handler and materialises no collection. The synthetic item is a length-prefixed byte blob whose
/// bytes the decoder copies into a pooled rental returned as the item's lease, so the tests exercise the
/// borrow-then-release lifetime directly: each item is valid only for its handler call, and the reader disposes
/// its lease the moment the handler returns — including when the handler throws. The frame-level and item-level
/// hostile-input bounds are pinned alongside the round-trip and padding paths.
/// </summary>
[TestClass]
internal sealed class ItemStreamChannelTests
{
    private const int MinimumItemByteLength = 4;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Writes a frame's worth of items: a four-byte big-endian count, then each item as a four-byte big-endian
    /// length prefix followed by its bytes.
    /// </summary>
    /// <remarks>
    /// The reader's decoder mirrors this exactly.
    /// </remarks>
    private static SerializeMessageDelegate<IReadOnlyList<byte[]>> SerializeBlobs { get; } =
        (items, output) =>
        {
            Span<byte> count = output.GetSpan(4)[..4];
            BinaryPrimitives.WriteUInt32BigEndian(count, (uint)items.Count);
            output.Advance(4);

            foreach(byte[] item in items)
            {
                Span<byte> length = output.GetSpan(4)[..4];
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)item.Length);
                output.Advance(4);
                output.Write(item);
            }
        };


    [TestMethod]
    public async Task RoundTripsItemsInOneFrame()
    {
        byte[][] items =
        [
            [0x01, 0x02, 0x03],
            [0xAA],
            [0xDE, 0xAD, 0xBE, 0xEF]
        ];

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllItems(reader).ConfigureAwait(false);

        string[] expected = [.. items.Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public async Task FlattensItemsAcrossMultipleFrames()
    {
        byte[][] first = [[0x01], [0x02, 0x03]];
        byte[][] second = [[0x04, 0x05, 0x06], [0x07]];

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await writer.WriteAsync(first, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(second, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllItems(reader).ConfigureAwait(false);

        string[] expected = [.. first.Concat(second).Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public async Task EmptyFrameYieldsNoItems()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await writer.WriteAsync([], TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllItems(reader).ConfigureAwait(false);

        Assert.IsEmpty(received);
    }


    [TestMethod]
    public void ConstructorRejectsNullArguments()
    {
        using BaseMemoryPool pool = new();
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new ItemStreamChannelReader<Blob>(null!, DecodeBlob, pool, MinimumItemByteLength));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ItemStreamChannelReader<Blob>(pipe.Reader, null!, pool, MinimumItemByteLength));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ItemStreamChannelReader<Blob>(pipe.Reader, DecodeBlob, null!, MinimumItemByteLength));
    }


    [TestMethod]
    public void ConstructorRejectsNonPositiveBounds()
    {
        using BaseMemoryPool pool = new();
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ItemStreamChannelReader<Blob>(pipe.Reader, DecodeBlob, pool, minimumItemByteLength: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ItemStreamChannelReader<Blob>(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength, maxFrameLength: 0));
    }


    [TestMethod]
    public async Task ReadAllAsyncRejectsNullHandler()
    {
        using BaseMemoryPool pool = new();
        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => reader.ReadAllAsync(null!, TestContext.CancellationToken).AsTask()).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task HostileItemCountFailsBeforeDecoding()
    {
        //A frame whose declared count could not fit even the minimum item bytes is rejected up front, before a
        //single item is decoded: count 0xFFFFFFFF against four payload bytes.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await WriteRawFrame(pipe.Writer, [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00]).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task TrailingBytesBeyondDeclaredItemsAreRejected()
    {
        //Count one, one empty item, then a stray byte: a well-formed frame ends exactly on its last item, so the
        //trailing byte is a framing fault.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await WriteRawFrame(pipe.Writer, [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xAB]).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ItemFieldLengthBeyondTheFrameIsRejected()
    {
        //Count one, an item length prefix of 100, but only ten bytes follow: the per-item field bound rejects it,
        //the bound the up-front count check cannot catch.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        byte[] payload = [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x64, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        await WriteRawFrame(pipe.Writer, payload).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ChannelEndingMidFrameThrows()
    {
        //The header promises ten payload bytes but the writer completes after three: a protocol violation.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        Memory<byte> frame = pipe.Writer.GetMemory(7)[..7];
        frame.Span.Clear();
        frame.Span[3] = 10;
        pipe.Writer.Advance(7);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task PaddedItemsRoundTrip()
    {
        byte[][] items =
        [
            [0x01, 0x02],
            [.. Enumerable.Repeat((byte)0x33, 40)],
            [0x44, 0x55, 0x66]
        ];

        FramePadding padding = FramePadding.PowersOfTwo(64);

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs, padding: padding);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength, padding: padding);

        await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllItems(reader).ConfigureAwait(false);

        string[] expected = [.. items.Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    [DoNotParallelize]
    public async Task PerItemLeaseReleaseLeavesNoActiveRentals()
    {
        //Each item's bytes are copied into a pooled rental disposed the moment its handler returns. Once the
        //whole stream is drained and the pool disposed, the rental ledger must balance — proving the reader, not
        //the consumer, releases each item's backing per item.
        byte[][] items =
        [
            [0x01, 0x02, 0x03],
            [0x04, 0x05, 0x06, 0x07],
            [0x08, 0x09]
        ];

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            Pipe pipe = new();
            MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
            ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

            await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);

            List<byte[]> received = await ReadAllItems(reader).ConfigureAwait(false);
            Assert.HasCount(items.Length, received);
        }

        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    [DoNotParallelize]
    public async Task LeaseReleasedEvenWhenHandlerThrows()
    {
        //A handler that throws part-way through the stream must still leave the rental ledger balanced: the
        //in-flight item's lease is disposed in the reader's per-item finally before the exception propagates.
        byte[][] items =
        [
            [0x01, 0x02, 0x03],
            [0x04, 0x05, 0x06, 0x07],
            [0x08, 0x09, 0x0A]
        ];

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            Pipe pipe = new();
            MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
            ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

            await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);

            int seen = 0;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => reader.ReadAllAsync(
                    (in Blob item) =>
                    {
                        seen++;
                        if(seen == 2)
                        {
                            throw new InvalidOperationException("the test handler refuses the second item");
                        }
                    },
                    TestContext.CancellationToken).AsTask()).ConfigureAwait(false);

            Assert.AreEqual(2, seen);
        }

        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public async Task ACancelMidFrameStillHandlesTheFrameInHandAndThenEndsTheReadTask()
    {
        //A cancel is consulted where a read is issued, not between the items of a frame already in hand, so a
        //handler that cancels while holding the first item still receives the rest of that frame. The stop takes
        //effect at the next read from the pipe, which ends the read task rather than faulting it.
        byte[][] items =
        [
            [0x01, 0x02, 0x03],
            [0x04]
        ];

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);

        var received = new List<byte[]>();
        Task read = reader.ReadAllAsync(
            (in Blob item) =>
            {
                received.Add(item.Bytes.ToArray());
                if(received.Count == 1)
                {
                    reader.CancelPendingRead();
                }
            },
            TestContext.CancellationToken).AsTask();

        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);

        string[] expected = [.. items.Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual, "The cancel dropped an item of the frame already in hand.");
    }


    /// <summary>
    /// Pins that <see cref="ItemStreamChannelReader{TItem}.ReadAllAsync"/> completes the underlying
    /// <see cref="PipeReader"/> in its finally block even when a protocol violation makes the read throw, so the
    /// writer side always observes the reader ending rather than waiting on an abandoned pipe.
    /// </summary>
    [TestMethod]
    public async Task ReadAllAsyncCompletesTheReaderWhenAProtocolViolationThrows()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        //A hostile count that cannot fit the remaining bytes makes ReadAllAsync throw before decoding
        //anything; the writer is deliberately never completed by this test, so any completion the writer
        //observes afterward must have come from the reader's own finally block.
        await WriteRawFrame(pipe.Writer, [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00]).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);

        FlushResult flushResult = await pipe.Writer.WriteAsync(new byte[] { 0x00 }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(flushResult.IsCompleted, "The reader must be completed once ReadAllAsync ends, even on the throwing path.");
    }


    /// <summary>
    /// Pins that a frame payload shorter than the four-byte item-count header is rejected before any count is
    /// read, distinct from the count-vs-remaining-bytes bound checked once a count has been read.
    /// </summary>
    [TestMethod]
    public async Task FrameShorterThanItemCountHeaderIsRejected()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        //A two-byte real payload cannot even hold the four-byte item-count header.
        await WriteRawFrame(pipe.Writer, [0x00, 0x00]).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins the up-front bound in the item-count check: a declared count that cannot fit the frame's remaining
    /// bytes under the reader's configured minimum item length is rejected before any item is decoded, even when
    /// the bytes actually present would otherwise decode cleanly as a smaller, coincidental minimum.
    /// </summary>
    [TestMethod]
    public async Task HostileItemCountAgainstAStricterMinimumIsRejectedBeforeDecoding()
    {
        //The reader is configured with a 20-byte minimum item length. Count declares one item, but only four
        //bytes remain after the count header (a zero-length item's own length prefix) — 1 * 20 = 20 exceeds
        //those 4 bytes, so the real bound must reject, even though those same 4 bytes alone would decode
        //cleanly as one empty item under integer division (1 / 20 = 0, which does not exceed 4).
        const int strictMinimumItemByteLength = 20;

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, strictMinimumItemByteLength);

        await WriteRawFrame(pipe.Writer, [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllItems(reader)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that a read genuinely parked mid-flight resumes off the thread pool instead of the caller's captured
    /// context, matching the reader's ConfigureAwait(false) on the pipe read and the guarantee its two sibling
    /// readers already carry, so a host that blocks the starting thread on ReadAllAsync cannot deadlock it.
    /// </summary>
    [TestMethod]
    public async Task ReadAllAsyncDoesNotResumeOnTheCallersContextAfterAPendingRead()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<IReadOnlyList<byte[]>> writer = new(pipe.Writer, SerializeBlobs);
        ItemStreamChannelReader<Blob> reader = new(pipe.Reader, DecodeBlob, pool, MinimumItemByteLength);

        PostCountingSynchronizationContext context = new();
        var received = new List<byte[]>();

        Task read = context.Start(() => reader.ReadAllAsync((in Blob item) => received.Add(item.Bytes.ToArray()), TestContext.CancellationToken).AsTask());

        //Nothing is written yet, so the read parked genuinely instead of completing synchronously.
        Assert.IsFalse(read.IsCompleted, "The read completed synchronously, so a captured continuation could not be observed.");

        byte[][] items = [[0x01, 0x02, 0x03]];
        await writer.WriteAsync(items, TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        await read.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, received);
        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// Decodes one length-prefixed blob, copying its bytes into a pooled rental returned as the lease so the item
    /// outlives the borrowed frame buffer.
    /// </summary>
    /// <remarks>
    /// An empty blob owns nothing, so its lease is null. Every field is bounded against the cursor before a byte
    /// is copied, and nothing is rented on a path that then throws.
    /// </remarks>
    private static Blob DecodeBlob(ref SequenceReader<byte> reader, MemoryPool<byte> pool, out IDisposable? lease)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        if(!reader.TryCopyTo(lengthBytes))
        {
            throw new InvalidOperationException("A blob item is shorter than its four-byte length prefix.");
        }

        reader.Advance(4);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

        if(length > reader.Remaining)
        {
            throw new InvalidOperationException("A blob item declares more bytes than the frame holds.");
        }

        if(length == 0)
        {
            lease = null;

            return new Blob(ReadOnlyMemory<byte>.Empty);
        }

        IMemoryOwner<byte> owner = pool.Rent((int)length);
        Memory<byte> destination = owner.Memory[..(int)length];
        if(!reader.TryCopyTo(destination.Span))
        {
            //Unreachable given the bound above, but returning the rental before throwing keeps the decoder
            //leak-free on every path, which is the contract the reader relies on.
            owner.Dispose();

            throw new InvalidOperationException("A blob item is truncated within the frame.");
        }

        reader.Advance(length);
        lease = owner;

        return new Blob(destination);
    }


    private async Task<List<byte[]>> ReadAllItems(ItemStreamChannelReader<Blob> reader)
    {
        var received = new List<byte[]>();
        await reader.ReadAllAsync((in Blob item) => received.Add(item.Bytes.ToArray()), TestContext.CancellationToken).ConfigureAwait(false);

        return received;
    }


    private async Task WriteRawFrame(PipeWriter writer, byte[] payload)
    {
        Memory<byte> header = writer.GetMemory(4)[..4];
        BinaryPrimitives.WriteUInt32BigEndian(header.Span, (uint)payload.Length);
        writer.Advance(4);
        writer.Write(payload);
        await writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// A decoded item viewing pooled bytes: valid only while its lease is undisposed, which is the duration of one
    /// handler call.
    /// </summary>
    private readonly record struct Blob(ReadOnlyMemory<byte> Bytes);
}
