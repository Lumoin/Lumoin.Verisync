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
/// Each frame is a four-byte big-endian length prefix followed by the serialized payload. This is the push
/// side of the seam: callers push messages in with <see cref="WriteAsync(TMessage, CancellationToken)"/>.
/// </remarks>
public sealed class MessageChannelWriter<TMessage>
{
    private const int FrameHeaderLength = 4;

    private PipeWriter Writer { get; }
    private SerializeMessageDelegate<TMessage> Serialize { get; }


    /// <summary>
    /// Initializes a new writer over <paramref name="writer"/>, serializing with <paramref name="serialize"/>.
    /// </summary>
    /// <param name="writer">The destination pipe writer.</param>
    /// <param name="serialize">The serializer for each message.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writer"/> or <paramref name="serialize"/> is <see langword="null"/>.</exception>
    public MessageChannelWriter(PipeWriter writer, SerializeMessageDelegate<TMessage> serialize)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serialize);
        Writer = writer;
        Serialize = serialize;
    }


    /// <summary>
    /// Serializes <paramref name="message"/> and writes it as one framed message, flushing the pipe.
    /// </summary>
    /// <param name="message">The message to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the frame has been flushed.</returns>
    public async ValueTask WriteAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        var payload = new ArrayBufferWriter<byte>();
        Serialize(message, payload);

        Span<byte> header = Writer.GetSpan(FrameHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.WrittenCount);
        Writer.Advance(FrameHeaderLength);
        Writer.Write(payload.WrittenSpan);

        await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Signals that no more messages will be written, completing the underlying pipe so a reader's
    /// enumeration ends.
    /// </summary>
    /// <returns>A task that completes when the pipe writer has been completed.</returns>
    public ValueTask CompleteAsync() => Writer.CompleteAsync();
}
