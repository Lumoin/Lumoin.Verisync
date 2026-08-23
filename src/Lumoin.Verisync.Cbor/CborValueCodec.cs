using Lumoin.Verisync.Core;
using System;
using System.Buffers;
using System.Formats.Cbor;

namespace Lumoin.Verisync.Cbor;

/// <summary>
/// Binds a format-neutral value codec to this library's CBOR codecs.
/// </summary>
/// <remarks>
/// The CBOR counterpart of the JSON binding, and deliberately the same shape: a caller supplies one
/// <see cref="EncodeValueDelegate{TValue}"/> and uses it with either format, rather than one codec per
/// format typed on that format's writer. The value travels as a CBOR byte string, which is what CBOR has for
/// bytes where JSON has base64 text.
/// </remarks>
public static class CborValueCodec
{
    /// <summary>Binds <paramref name="encode"/> to the CBOR writer the message factories expect.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="encode">Writes the value's bytes.</param>
    /// <returns>A writer the CBOR message factories accept.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="encode"/> is <see langword="null"/>.</exception>
    public static WriteValueDelegate<CborWriter, TValue> CreateWriter<TValue>(EncodeValueDelegate<TValue> encode)
    {
        ArgumentNullException.ThrowIfNull(encode);

        return (writer, value) =>
        {
            ArrayBufferWriter<byte> bytes = new();
            encode(value, bytes);
            writer.WriteByteString(bytes.WrittenSpan);
        };
    }


    /// <summary>Binds <paramref name="decode"/> to the CBOR reader the message factories read from.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="decode">Reads the value from its bytes.</param>
    /// <returns>A reader the CBOR message factories accept.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="decode"/> is <see langword="null"/>.</exception>
    public static ReadValueDelegate<CborReader, TValue> CreateReader<TValue>(DecodeValueDelegate<TValue> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);

        return source => decode(source.ReadByteString());
    }
}
