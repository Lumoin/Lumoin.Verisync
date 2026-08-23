using Lumoin.Verisync.Core;
using System;
using System.Buffers;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Binds a format-neutral value codec to this library's JSON codecs.
/// </summary>
/// <remarks>
/// <para>
/// The message factories take a value codec typed on <see cref="Utf8JsonWriter"/> and
/// <see cref="JsonElement"/>, which is what native shaping costs: a caller that supplies one names
/// System.Text.Json types in its own project, and writes a second codec for CBOR because the two
/// instantiations are unrelated types. A caller that only needs its value carried, not shaped, supplies an
/// <see cref="EncodeValueDelegate{TValue}"/> here instead and never names a serialization type at all.
/// </para>
/// <para>
/// The value travels as a base64 string, which is what JSON has for bytes. That is the only difference from
/// the CBOR binding, which uses a byte string; the same neutral codec serves both.
/// </para>
/// </remarks>
public static class JsonValueCodec
{
    /// <summary>Binds <paramref name="encode"/> to the JSON writer the message factories expect.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="encode">Writes the value's bytes.</param>
    /// <returns>A writer the JSON message factories accept.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="encode"/> is <see langword="null"/>.</exception>
    public static WriteValueDelegate<Utf8JsonWriter, TValue> CreateWriter<TValue>(EncodeValueDelegate<TValue> encode)
    {
        ArgumentNullException.ThrowIfNull(encode);

        return (writer, value) =>
        {
            ArrayBufferWriter<byte> bytes = new();
            encode(value, bytes);
            writer.WriteBase64StringValue(bytes.WrittenSpan);
        };
    }


    /// <summary>Binds <paramref name="decode"/> to the JSON element the message factories read from.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="decode">Reads the value from its bytes.</param>
    /// <returns>A reader the JSON message factories accept.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="decode"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">Thrown by the returned reader when the slot does not carry base64 text.</exception>
    public static ReadValueDelegate<JsonElement, TValue> CreateReader<TValue>(DecodeValueDelegate<TValue> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);

        return source =>
        {
            //A JsonElement exposes no raw UTF-8, so the base64 text cannot be decoded into a rented buffer
            //from here and one array per value is the floor this API allows. Removing it means moving the
            //read seam from JsonElement to Utf8JsonReader, which is a change to every reader in this
            //assembly rather than to this binding.
            if(!source.TryGetBytesFromBase64(out byte[]? bytes))
            {
                throw new JsonException("A value slot written by the neutral value codec carries base64 text, and this one does not.");
            }

            return decode(bytes);
        };
    }
}
