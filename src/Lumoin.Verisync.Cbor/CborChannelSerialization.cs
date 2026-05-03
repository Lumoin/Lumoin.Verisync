using System;
using System.Buffers;
using System.Formats.Cbor;
using Lumoin.Verisync.Core;

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
    public static SerializeMessageDelegate<TMessage> CreateSerializer<TMessage>(Action<CborWriter, TMessage> encode)
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
    public static DeserializeMessageDelegate<TMessage> CreateDeserializer<TMessage>(Func<CborReader, TMessage> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);

        return payload =>
        {
            var cborReader = new CborReader(payload.ToArray(), CborConformanceMode.Canonical);

            return decode(cborReader);
        };
    }
}
