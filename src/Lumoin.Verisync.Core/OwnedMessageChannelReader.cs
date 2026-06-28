using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads length-prefixed framed messages from a <see cref="PipeReader"/>, deserializing each into an
/// <em>owned</em> value through an injected <see cref="DeserializeOwnedMessageDelegate{TMessage}"/> backed by a
/// supplied <see cref="MemoryPool{T}"/>. It is the pool-aware companion to
/// <see cref="MessageChannelReader{TMessage}"/>: identical framing, padding, and hostile-frame bounds, but the
/// value out owns pooled memory instead of allocating on the GC heap, which suits the "one framed blob in, one
/// owned payload out" case (a sketch image, any single byte payload the consumer keeps and then verifies).
/// </summary>
/// <typeparam name="TMessage">The owned message type — a value that holds pooled buffers and is the consumer's to dispose.</typeparam>
/// <remarks>
/// <para>
/// <strong>Ownership transfers to the consumer.</strong> Each value this reader yields is the caller's to
/// dispose; the reader never disposes a yielded value. A consumer that processes and discards each message
/// releases it per message by wrapping the loop body in a <see langword="using"/>; a consumer that keeps the
/// message — the sketch-image client that hands the blob up the stack — disposes it once it is done. The
/// deserializer copies the payload into pool-backed memory before the value is yielded, so a yielded value is
/// independent of the channel buffer the reader recycles.
/// </para>
/// <para>
/// Framing, padding, and the attacker-facing bounds are exactly those of
/// <see cref="MessageChannelReader{TMessage}"/>: a pipe that ends part-way through a frame throws, as does a
/// frame whose declared length exceeds the configured maximum, and a configured
/// <see cref="FramePadding"/> policy must match the writing peer's.
/// </para>
/// </remarks>
public sealed class OwnedMessageChannelReader<TMessage>
{
    private PipeReader Reader { get; }
    private DeserializeOwnedMessageDelegate<TMessage> Deserialize { get; }
    private MemoryPool<byte> Pool { get; }
    private int MaxFrameLength { get; }
    private FramePadding? Padding { get; }


    /// <summary>
    /// Initializes a new reader over <paramref name="reader"/>, deserializing with <paramref name="deserialize"/>
    /// into pooled memory from <paramref name="pool"/>.
    /// </summary>
    /// <param name="reader">The source pipe reader.</param>
    /// <param name="deserialize">The pool-aware deserializer for each framed payload.</param>
    /// <param name="pool">The pool each deserialized value rents its backing from. Required and non-null, so memory provenance is explicit at every reader.</param>
    /// <param name="maxFrameLength">The largest frame payload accepted, in bytes. Defaults to <see cref="MessageChannel.DefaultMaxFrameLength"/>. When <paramref name="padding"/> is supplied this bounds the padded length, matching the writing peer.</param>
    /// <param name="padding">An optional padding policy that must match the writing peer's. When <see langword="null"/> the reader expects the unpadded wire format, byte for byte.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reader"/>, <paramref name="deserialize"/>, or <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxFrameLength"/> is less than one.</exception>
    public OwnedMessageChannelReader(PipeReader reader, DeserializeOwnedMessageDelegate<TMessage> deserialize, MemoryPool<byte> pool, int maxFrameLength = MessageChannel.DefaultMaxFrameLength, FramePadding? padding = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameLength, 1);
        Reader = reader;
        Deserialize = deserialize;
        Pool = pool;
        MaxFrameLength = maxFrameLength;
        Padding = padding;
    }


    /// <summary>
    /// Reads and deserializes every framed message until the channel ends. Ownership of each yielded value
    /// transfers to the consumer, which must dispose it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of owned, deserialized messages.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the channel ends part-way through a frame, or a frame declares a payload longer than the configured maximum.</exception>
    public async IAsyncEnumerable<TMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        //Completion runs even when the deserializer or a protocol violation throws, so the writer side
        //always observes the reader ending instead of waiting on an abandoned pipe.
        try
        {
            while(true)
            {
                ReadResult result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while(FrameReader.TryReadFrame(ref buffer, MaxFrameLength, out ReadOnlySequence<byte> frame))
                {
                    //The deserializer copies into pool-backed memory before the value is yielded, so the value
                    //is valid after the channel buffer is advanced and recycled below.
                    yield return Deserialize(FrameReader.RealPayload(frame, Padding), Pool);
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
}
