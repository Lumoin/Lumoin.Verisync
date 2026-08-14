using Lumoin.Verisync.Core;
using System;
using System.Buffers;
using System.Formats.Cbor;

namespace Lumoin.Verisync.Cbor;

/// <summary>
/// Builds <see cref="SerializeMessageDelegate{TMessage}"/> and <see cref="DeserializeMessageDelegate{TMessage}"/>
/// implementations backed by <see cref="System.Formats.Cbor"/>, for plugging CBOR into a Verisync message channel.
/// </summary>
/// <remarks>
/// CBOR has no reflection-based serializer, so the caller supplies the per-type field encoding and decoding;
/// this class handles the channel buffer plumbing and uses <see cref="CborConformanceMode.Canonical"/> so the
/// same message always produces the same bytes — the determinism digests and chain linkage depend on.
/// </remarks>
public static class CborChannelSerialization
{
    /// <summary>
    /// Creates a CBOR serializer from a caller-supplied field encoder.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="encode">Writes the message's fields to a <see cref="CborWriter"/>.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="encode"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<TMessage> CreateSerializer<TMessage>(WriteValueDelegate<CborWriter, TMessage> encode)
    {
        ArgumentNullException.ThrowIfNull(encode);

        return (message, output) =>
        {
            var cborWriter = new CborWriter(CborConformanceMode.Canonical);
            encode(cborWriter, message);
            byte[] encoded = cborWriter.Encode();
            output.Write(encoded);
        };
    }


    /// <summary>
    /// Creates a CBOR deserializer from a caller-supplied field decoder.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="decode">Reads the message's fields from a <see cref="CborReader"/>.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="decode"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<TMessage> CreateDeserializer<TMessage>(ReadValueDelegate<CborReader, TMessage> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);

        return CborMessageGuard.FailClosed<TMessage>(payload =>
        {
            var cborReader = new CborReader(payload.ToArray(), CborConformanceMode.Canonical);
            TMessage message = decode(cborReader);

            //Surplus bytes after the message are refused rather than ignored. Allowing them would let several
            //distinct byte sequences decode to one message, which is the same canonical-bytes assumption the
            //JSON channel refuses trailing data to keep, and which the determinism this class is built for
            //depends on. Frames are length prefixed, so anything left here is slack the sender chose to add.
            if(cborReader.BytesRemaining != 0)
            {
                throw new CborContentException("The CBOR payload carries trailing data after the message.");
            }

            return message;
        });
    }
}
