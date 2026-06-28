using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads a length-prefixed flow of one structured item type from a <see cref="PipeReader"/> and drives it
/// through a per-item handler, materialising no collection. Where
/// <see cref="MessageChannelReader{TMessage}"/> models discrete, heterogeneous messages — "one value out" —
/// this models the other shape the transports need: a stream of homogeneous items (reconcile keys, content
/// triples) the consumer processes one at a time and never needs whole. Item bytes come from an injected
/// <see cref="MemoryPool{T}"/>, released as each item is consumed.
/// </summary>
/// <typeparam name="TItem">The item type yielded to the handler.</typeparam>
/// <remarks>
/// <para>
/// Wire format: each frame's real payload is a four-byte big-endian item count followed by that many items,
/// each encoded by the paired writer's per-item serializer. A frame is the unit of framing; the reader
/// flattens the items of every frame into one stream, so a writer may send the whole flow as a single
/// count-prefixed frame or as several. An empty flow is a count of zero, which is valid — a peer with nothing
/// to send is not a fault.
/// </para>
/// <para>
/// <strong>Items are borrowed.</strong> Each item is valid only for the duration of its handler call; the
/// pooled backing the decoder rents for it is disposed the moment the handler returns. A handler that must
/// retain an item copies or interns it before returning. This deterministic per-item release is why the reader
/// drives a handler rather than yielding an <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>: the
/// lifetime of each item's memory is bounded by exactly one call.
/// </para>
/// <para>
/// The attacker-facing bounds match the rest of the channel. The outer frame length is capped at the
/// configured maximum; within a frame the declared item count is rejected up front when its items could not
/// fit the bytes present, before any item is decoded, and the decoder bounds each field length against the
/// bytes remaining; a frame carrying bytes beyond its declared items is rejected; and a pipe that ends
/// part-way through a frame throws. A configured <see cref="FramePadding"/> policy must match the writing
/// peer's.
/// </para>
/// </remarks>
public sealed class ItemStreamChannelReader<TItem>
{
    private PipeReader Reader { get; }
    private DecodeItemDelegate<TItem> DecodeItem { get; }
    private MemoryPool<byte> Pool { get; }
    private int MinimumItemByteLength { get; }
    private int MaxFrameLength { get; }
    private FramePadding? Padding { get; }


    /// <summary>
    /// Initializes a new reader over <paramref name="reader"/>, decoding each item with
    /// <paramref name="decodeItem"/> against memory from <paramref name="pool"/>.
    /// </summary>
    /// <param name="reader">The source pipe reader.</param>
    /// <param name="decodeItem">The per-item decoder.</param>
    /// <param name="pool">The pool each item's backing is rented from. Required and non-null, so memory provenance is explicit at every reader.</param>
    /// <param name="minimumItemByteLength">The smallest number of bytes one item can occupy on the wire. Used to reject a hostile item count before any item is decoded: the count times this minimum must fit the frame's remaining bytes.</param>
    /// <param name="maxFrameLength">The largest frame payload accepted, in bytes. Defaults to <see cref="MessageChannel.DefaultMaxFrameLength"/>. When <paramref name="padding"/> is supplied this bounds the padded length, matching the writing peer.</param>
    /// <param name="padding">An optional padding policy that must match the writing peer's. When <see langword="null"/> the reader expects the unpadded wire format, byte for byte.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reader"/>, <paramref name="decodeItem"/>, or <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="minimumItemByteLength"/> or <paramref name="maxFrameLength"/> is less than one.</exception>
    public ItemStreamChannelReader(PipeReader reader, DecodeItemDelegate<TItem> decodeItem, MemoryPool<byte> pool, int minimumItemByteLength, int maxFrameLength = MessageChannel.DefaultMaxFrameLength, FramePadding? padding = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(decodeItem);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumItemByteLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameLength, 1);
        Reader = reader;
        DecodeItem = decodeItem;
        Pool = pool;
        MinimumItemByteLength = minimumItemByteLength;
        MaxFrameLength = maxFrameLength;
        Padding = padding;
    }


    /// <summary>
    /// Reads every item of every frame until the channel ends, invoking <paramref name="handleItem"/> once per
    /// item. Each item is valid only for the duration of its handler call.
    /// </summary>
    /// <param name="handleItem">The per-item handler.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the channel ends.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="handleItem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the channel ends part-way through a frame, a frame declares more items than its bytes can hold, or a frame carries bytes beyond its declared items.</exception>
    public async ValueTask ReadAllAsync(ItemHandlerDelegate<TItem> handleItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handleItem);

        //Completion runs even when the decoder, the handler, or a protocol violation throws, so the writer side
        //always observes the reader ending instead of waiting on an abandoned pipe.
        try
        {
            while(true)
            {
                ReadResult result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while(FrameReader.TryReadFrame(ref buffer, MaxFrameLength, out ReadOnlySequence<byte> frame))
                {
                    ReadItems(FrameReader.RealPayload(frame, Padding), handleItem);
                }

                Reader.AdvanceTo(buffer.Start, buffer.End);

                if(result.IsCompleted)
                {
                    if(!buffer.IsEmpty)
                    {
                        throw new InvalidOperationException("The channel ended part-way through a frame.");
                    }

                    break;
                }
            }
        }
        finally
        {
            await Reader.CompleteAsync().ConfigureAwait(false);
        }
    }


    private void ReadItems(ReadOnlySequence<byte> payload, ItemHandlerDelegate<TItem> handleItem)
    {
        var reader = new SequenceReader<byte>(payload);

        Span<byte> countBytes = stackalloc byte[FrameReader.FrameHeaderLength];
        if(!reader.TryCopyTo(countBytes))
        {
            throw new InvalidOperationException("An item-stream frame is shorter than its four-byte item count.");
        }

        reader.Advance(FrameReader.FrameHeaderLength);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(countBytes);

        //The count is attacker-influenced: reject up front any count whose items could not fit the bytes
        //present, before a single item is decoded, exactly as the eager readers bounded the count against the
        //payload before allocating the collection.
        if((long)count * MinimumItemByteLength > reader.Remaining)
        {
            throw new InvalidOperationException($"An item-stream frame declares {count} items, more than its {reader.Remaining} remaining bytes can hold; the peer is faulty, hostile, or speaking another protocol.");
        }

        for(uint index = 0; index < count; index++)
        {
            //The decoder bounds each field against the cursor before it copies. The item's pooled backing, if
            //any, is released the moment the handler returns, so the bytes live exactly as long as the item.
            TItem item = DecodeItem(ref reader, Pool, out IDisposable? lease);
            try
            {
                handleItem(in item);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        //A well-formed frame ends exactly on its last item; trailing bytes are a framing or protocol fault, so
        //they are rejected rather than silently ignored.
        if(reader.Remaining != 0)
        {
            throw new InvalidOperationException("An item-stream frame carried bytes beyond its declared items; the peer is faulty, hostile, or speaking another protocol.");
        }
    }
}
