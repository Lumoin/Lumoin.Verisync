using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads length-prefixed framed messages from a <see cref="PipeReader"/>, deserializing each through an
/// injected <see cref="DeserializeMessageDelegate{TMessage}"/> and surfacing them as an
/// <see cref="IAsyncEnumerable{T}"/>. The pipe may be backed by a socket, an in-memory <see cref="Pipe"/>,
/// or any duplex stream — the reader is channel-agnostic.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <remarks>
/// <para>
/// Enumeration ends when the pipe is completed by the writer, when the token is signalled, or when
/// <see cref="CancelPendingRead"/> cancels a read. The last is the consumer's own graceful stop: the canceled
/// read itself yields nothing and ends the enumeration without the partial-frame check, because the consumer
/// ended the stream rather than the peer. Frames already read from the pipe into the buffer in hand are still
/// yielded until that buffer is exhausted, so the stop takes effect at the next read from the pipe; a consumer
/// that must not observe further messages stops consuming rather than relying on the cancel. A pipe that ends
/// part-way through a frame is a protocol
/// violation and throws, as is a frame whose declared length exceeds the configured maximum — the length
/// prefix is attacker-controlled on an untrusted transport, so it is never trusted beyond that bound.
/// </para>
/// <para>
/// With a <see cref="FramePadding"/> policy, the outer prefix declares a padded bucket length and the frame
/// itself begins with a four-byte big-endian <em>real</em> length prefix; the reader deserializes only the
/// real payload slice and discards the zero fill — see <see cref="FramePadding"/> for the wire format. The
/// inner length is just as attacker-influenced as the outer prefix, so it is rejected the moment it would
/// reach past the frame bounds. The writing peer must be configured with the same policy, exactly as it
/// must share the maximum frame length: a mismatch yields a deserialization failure, not a clean error.
/// </para>
/// </remarks>
public sealed class MessageChannelReader<TMessage>
{
    private PipeReader Reader { get; }
    private DeserializeMessageDelegate<TMessage> Deserialize { get; }
    private int MaxFrameLength { get; }
    private FramePadding? Padding { get; }


    /// <summary>
    /// Initializes a new reader over <paramref name="reader"/>, deserializing with <paramref name="deserialize"/>.
    /// </summary>
    /// <param name="reader">The source pipe reader.</param>
    /// <param name="deserialize">The deserializer for each framed payload.</param>
    /// <param name="maxFrameLength">The largest frame payload accepted, in bytes. Defaults to <see cref="MessageChannel.DefaultMaxFrameLength"/>. When <paramref name="padding"/> is supplied this bounds the padded length, matching the writing peer.</param>
    /// <param name="padding">An optional padding policy that must match the writing peer's. When <see langword="null"/> the reader expects the unpadded wire format, byte for byte. A policy mismatch deserializes the wrong span and so fails, exactly as a maximum-frame-length mismatch does.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reader"/> or <paramref name="deserialize"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxFrameLength"/> is less than one.</exception>
    public MessageChannelReader(PipeReader reader, DeserializeMessageDelegate<TMessage> deserialize, int maxFrameLength = MessageChannel.DefaultMaxFrameLength, FramePadding? padding = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameLength, 1);
        Reader = reader;
        Deserialize = deserialize;
        MaxFrameLength = maxFrameLength;
        Padding = padding;
    }


    /// <summary>
    /// Reads and deserializes every framed message until the channel ends.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of deserialized messages.</returns>
    /// <remarks>
    /// The channel ends three ways: the writer completes the pipe, the token is signalled, or
    /// <see cref="CancelPendingRead"/> cancels a read. The canceled read yields no message and ends the
    /// enumeration without the partial-frame check, while frames already taken from the pipe into the buffer
    /// in hand are yielded until that buffer is exhausted, so the stop takes effect at the next read from the
    /// pipe.
    /// </remarks>
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
                if(result.IsCanceled)
                {
                    Reader.AdvanceTo(result.Buffer.Start);

                    break;
                }

                ReadOnlySequence<byte> buffer = result.Buffer;

                while(FrameReader.TryReadFrame(ref buffer, MaxFrameLength, out ReadOnlySequence<byte> frame))
                {
                    yield return Deserialize(FrameReader.RealPayload(frame, Padding));
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


    /// <summary>
    /// Cancels the read in flight, or the next one, without completing the pipe. The enumeration then ends
    /// gracefully: a pending MoveNextAsync completes false, so a consumer stopping mid-stream can observe the
    /// end and dispose the enumerator. Callable from any thread, and tolerated after the enumeration has
    /// already ended.
    /// </summary>
    public void CancelPendingRead() => Reader.CancelPendingRead();
}
