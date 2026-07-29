using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.IO.Pipelines;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Tests for <see cref="OwnedMessageChannelReader{TMessage}"/> — the pool-aware companion to
/// <see cref="MessageChannelReader{TMessage}"/> whose deserialized value owns pooled memory. The owned message
/// type here is a bare <see cref="IMemoryOwner{T}"/> of <see cref="byte"/> (the sketch-image shape): the
/// deserializer copies the framed payload into a rental from the supplied pool, an empty payload deserializes
/// to the shared <see cref="EmptyMemoryOwner"/> with no rental, and ownership of every yielded value transfers
/// to the consumer, which disposes it. The framing, padding, and hostile-frame bounds it shares with the plain
/// reader are re-pinned here so the refactor onto <c>FrameReader</c> cannot regress them on this path.
/// </summary>
[TestClass]
internal sealed class OwnedMessageChannelTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SerializeMessageDelegate<byte[]> SerializeBytes { get; } =
        (message, output) => output.Write(message);

    //The pool-aware deserializer: an empty frame is the allocation-free empty owner, anything else is copied
    //into an exact-size rental from the supplied pool whose ownership the consumer takes.
    private static DeserializeOwnedMessageDelegate<IMemoryOwner<byte>> DeserializeOwned { get; } =
        (payload, pool) =>
        {
            if(payload.Length == 0)
            {
                return EmptyMemoryOwner.Instance;
            }

            IMemoryOwner<byte> owner = pool.Rent((int)payload.Length);
            payload.CopyTo(owner.Memory.Span);

            return owner;
        };


    [TestMethod]
    public async Task RoundTripsOwnedPayloadsOverInMemoryPipe()
    {
        byte[][] messages =
        [
            [0x01, 0x02, 0x03],
            [],
            [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11]
        ];

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes);
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool);

        foreach(byte[] message in messages)
        {
            await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllOwned(reader).ConfigureAwait(false);

        string[] expected = [.. messages.Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public async Task EmptyChannelYieldsNoMessages()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes);
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool);

        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllOwned(reader).ConfigureAwait(false);

        Assert.HasCount(0, received);
    }


    [TestMethod]
    public void ConstructorRejectsNullArguments()
    {
        using BaseMemoryPool pool = new();
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new OwnedMessageChannelReader<IMemoryOwner<byte>>(null!, DeserializeOwned, pool));
        Assert.ThrowsExactly<ArgumentNullException>(() => new OwnedMessageChannelReader<IMemoryOwner<byte>>(pipe.Reader, null!, pool));
        Assert.ThrowsExactly<ArgumentNullException>(() => new OwnedMessageChannelReader<IMemoryOwner<byte>>(pipe.Reader, DeserializeOwned, null!));
    }


    [TestMethod]
    public void ConstructorRejectsNonPositiveFrameLimit()
    {
        using BaseMemoryPool pool = new();
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OwnedMessageChannelReader<IMemoryOwner<byte>>(pipe.Reader, DeserializeOwned, pool, 0));
    }


    [TestMethod]
    public async Task HostileLengthPrefixFailsTheChannelInsteadOfBuffering()
    {
        //A peer claiming a ~4 GiB frame with a four-byte header must fail the read immediately; the declared
        //length is attacker-controlled and is never trusted past the configured maximum.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool);

        Memory<byte> header = pipe.Writer.GetMemory(4)[..4];
        header.Span.Fill(0xFF);
        pipe.Writer.Advance(4);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllOwned(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task FrameAboveTheConfiguredMaximumIsRejected()
    {
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes);
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool, maxFrameLength: 8);

        await writer.WriteAsync([0, 1, 2, 3, 4, 5, 6, 7, 8], TestContext.CancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllOwned(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ChannelEndingMidFrameThrows()
    {
        //The header promises ten payload bytes but the writer completes after three: a protocol violation.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool);

        Memory<byte> frame = pipe.Writer.GetMemory(7)[..7];
        frame.Span.Clear();
        frame.Span[3] = 10;
        pipe.Writer.Advance(7);
        await pipe.Writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReadAllOwned(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task PaddedOwnedPayloadsRoundTrip()
    {
        byte[][] messages =
        [
            [0x01],
            [.. Enumerable.Repeat((byte)0x07, 59)],
            [.. Enumerable.Repeat((byte)0x09, 200)]
        ];

        FramePadding padding = FramePadding.PowersOfTwo(64);

        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes, padding: padding);
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool, padding: padding);

        foreach(byte[] message in messages)
        {
            await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);

        List<byte[]> received = await ReadAllOwned(reader).ConfigureAwait(false);

        string[] expected = [.. messages.Select(Convert.ToHexString)];
        string[] actual = [.. received.Select(Convert.ToHexString)];
        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public async Task DeserializerFailureSurfacesAsMessageDeserializationException()
    {
        //A deserializer that fails closed on a sentinel payload must surface its failure through the reader, and
        //the reader must still complete its pipe so the writer side is not left waiting.
        using BaseMemoryPool pool = new();

        Pipe pipe = new();
        MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes);
        OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(
            pipe.Reader,
            (payload, _) => throw new MessageDeserializationException("rejected by the test deserializer"),
            pool);

        await writer.WriteAsync([0x01, 0x02], TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<MessageDeserializationException>(() => ReadAllOwned(reader)).ConfigureAwait(false);
    }


    [TestMethod]
    [DoNotParallelize]
    public async Task ConsumerDisposalLeavesNoActiveRentals()
    {
        //Every owned payload the reader yields is rented from the pool and disposed by the consumer; once the
        //consumer has disposed them all and the pool is disposed, the rental ledger must balance — a leak here
        //would mean the consumer-disposes-each contract is not actually honoured by the read path.
        byte[][] messages =
        [
            [0x01, 0x02, 0x03],
            [0x04, 0x05, 0x06, 0x07],
            [0x08, 0x09, 0x0A, 0x0B, 0x0C]
        ];

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            Pipe pipe = new();
            MessageChannelWriter<byte[]> writer = new(pipe.Writer, SerializeBytes);
            OwnedMessageChannelReader<IMemoryOwner<byte>> reader = new(pipe.Reader, DeserializeOwned, pool);

            foreach(byte[] message in messages)
            {
                await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);

            List<byte[]> received = await ReadAllOwned(reader).ConfigureAwait(false);
            Assert.HasCount(messages.Length, received);
        }

        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    //Drains the reader, copying each owned payload's bytes out and disposing the owner inside the loop — the
    //per-message release the class documents. The copy is what a consumer that does not retain the payload does.
    private async Task<List<byte[]>> ReadAllOwned(OwnedMessageChannelReader<IMemoryOwner<byte>> reader)
    {
        var received = new List<byte[]>();
        await foreach(IMemoryOwner<byte> owner in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            using(owner)
            {
                received.Add(owner.Memory.ToArray());
            }
        }

        return received;
    }
}
