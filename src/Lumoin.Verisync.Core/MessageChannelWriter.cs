using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Writes messages to a <see cref="PipeWriter"/> as length-prefixed frames, serializing each through an
/// injected <see cref="SerializeMessageDelegate{TMessage}"/>. The pipe may be backed by a socket, an
/// in-memory <see cref="Pipe"/>, or any duplex stream — the writer is channel-agnostic.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <remarks>
/// <para>
/// Without padding, each frame is a four-byte big-endian length prefix followed by the serialized payload.
/// This is the push side of the seam: callers push messages in with
/// <see cref="WriteAsync(TMessage, CancellationToken)"/>.
/// </para>
/// <para>
/// With a <see cref="FramePadding"/> policy, the outer four-byte prefix instead declares the <em>padded</em>
/// length (a bucket size), and the padded payload is a four-byte big-endian <em>real</em> length prefix, the
/// real payload bytes, then zero fill to the bucket. This quantizes the wire length so an observer cannot
/// distinguish message types by frame size; see <see cref="FramePadding"/> for the full wire format. The
/// reading peer must be configured with the same policy, exactly as it must share
/// <c>maxFrameLength</c>: a mismatch yields a deserialization failure, not a clean error.
/// </para>
/// </remarks>
public sealed class MessageChannelWriter<TMessage>
{
    private const int FrameHeaderLength = 4;

    private PipeWriter Writer { get; }
    private SerializeMessageDelegate<TMessage> Serialize { get; }
    private int MaxFrameLength { get; }
    private FramePadding? Padding { get; }


    /// <summary>
    /// Initializes a new writer over <paramref name="writer"/>, serializing with <paramref name="serialize"/>.
    /// </summary>
    /// <param name="writer">The destination pipe writer.</param>
    /// <param name="serialize">The serializer for each message.</param>
    /// <param name="maxFrameLength">The largest frame payload produced, in bytes. Defaults to <see cref="MessageChannel.DefaultMaxFrameLength"/>. Match the reading peer's limit: a compliant reader fails the connection on an oversized frame, so failing here is the friendlier error. When <paramref name="padding"/> is supplied this bounds the padded length, not the real payload.</param>
    /// <param name="padding">An optional padding policy that quantizes the wire length to size buckets to hide message types from a network observer. When <see langword="null"/> the wire format is exactly the unpadded format, byte for byte. The reading peer must be configured with the same policy.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writer"/> or <paramref name="serialize"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxFrameLength"/> is less than one.</exception>
    public MessageChannelWriter(PipeWriter writer, SerializeMessageDelegate<TMessage> serialize, int maxFrameLength = MessageChannel.DefaultMaxFrameLength, FramePadding? padding = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameLength, 1);
        Writer = writer;
        Serialize = serialize;
        MaxFrameLength = maxFrameLength;
        Padding = padding;
    }


    /// <summary>
    /// Serializes <paramref name="message"/> and writes it as one framed message, flushing the pipe.
    /// </summary>
    /// <param name="message">The message to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the frame has been flushed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the frame — the serialized payload, or its padded length when a <see cref="FramePadding"/> policy is configured — is longer than the configured maximum frame length.</exception>
    public async ValueTask WriteAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        var payload = new ArrayBufferWriter<byte>();
        Serialize(message, payload);

        if(Padding is null)
        {
            if(payload.WrittenCount > MaxFrameLength)
            {
                throw new InvalidOperationException($"The serialized message is {payload.WrittenCount} bytes, above the maximum frame payload of {MaxFrameLength}.");
            }

            Span<byte> header = Writer.GetSpan(FrameHeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.WrittenCount);
            Writer.Advance(FrameHeaderLength);
            Writer.Write(payload.WrittenSpan);
        }
        else
        {
            int paddedLength = Padding.PaddedLength(payload.WrittenCount);
            if(paddedLength > MaxFrameLength)
            {
                throw new InvalidOperationException($"The padded frame is {paddedLength} bytes, above the maximum frame payload of {MaxFrameLength}.");
            }

            //The outer prefix declares the padded length, the only quantity a network observer can measure.
            Span<byte> outerHeader = Writer.GetSpan(FrameHeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(outerHeader, (uint)paddedLength);
            Writer.Advance(FrameHeaderLength);

            //Inside the bucket: the real length, the real payload, then zero fill to the bucket boundary.
            Span<byte> innerHeader = Writer.GetSpan(FrameHeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(innerHeader, (uint)payload.WrittenCount);
            Writer.Advance(FrameHeaderLength);
            Writer.Write(payload.WrittenSpan);

            int fillLength = paddedLength - FrameHeaderLength - payload.WrittenCount;
            if(fillLength > 0)
            {
                Span<byte> fill = Writer.GetSpan(fillLength)[..fillLength];
                fill.Clear();
                Writer.Advance(fillLength);
            }
        }

        await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Signals that no more messages will be written, completing the underlying pipe so a reader's
    /// enumeration ends.
    /// </summary>
    /// <returns>A task that completes when the pipe writer has been completed.</returns>
    public ValueTask CompleteAsync() => Writer.CompleteAsync();
}
