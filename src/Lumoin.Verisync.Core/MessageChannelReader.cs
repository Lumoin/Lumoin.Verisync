using System;
using System.Buffers;
using System.Buffers.Binary;
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
/// Enumeration ends when the pipe is completed by the writer or the token is signalled. A pipe that ends
/// part-way through a frame is a protocol violation and throws.
/// </remarks>
public sealed class MessageChannelReader<TMessage>
{
    private const int FrameHeaderLength = 4;

    private PipeReader Reader { get; }
    private DeserializeMessageDelegate<TMessage> Deserialize { get; }


    /// <summary>
    /// Initializes a new reader over <paramref name="reader"/>, deserializing with <paramref name="deserialize"/>.
    /// </summary>
    /// <param name="reader">The source pipe reader.</param>
    /// <param name="deserialize">The deserializer for each framed payload.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reader"/> or <paramref name="deserialize"/> is <see langword="null"/>.</exception>
    public MessageChannelReader(PipeReader reader, DeserializeMessageDelegate<TMessage> deserialize)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(deserialize);
        Reader = reader;
        Deserialize = deserialize;
    }


    /// <summary>
    /// Reads and deserializes every framed message until the channel ends.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of deserialized messages.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the channel ends part-way through a frame.</exception>
    public async IAsyncEnumerable<TMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while(true)
        {
            ReadResult result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while(TryReadFrame(ref buffer, out ReadOnlySequence<byte> frame))
            {
                yield return Deserialize(frame);
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

        await Reader.CompleteAsync().ConfigureAwait(false);
    }


    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if(buffer.Length < FrameHeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        buffer.Slice(0, FrameHeaderLength).CopyTo(header);
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

        if(buffer.Length < FrameHeaderLength + payloadLength)
        {
            return false;
        }

        frame = buffer.Slice(FrameHeaderLength, payloadLength);
        buffer = buffer.Slice(FrameHeaderLength + payloadLength);

        return true;
    }
}
