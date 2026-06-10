using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds <see cref="SerializeMessageDelegate{TMessage}"/> and <see cref="DeserializeMessageDelegate{TMessage}"/>
/// implementations backed by <see cref="System.Text.Json"/>, for plugging JSON into a Verisync message channel.
/// </summary>
/// <remarks>
/// Both factories take a source-generated <see cref="JsonTypeInfo{T}"/> so serialization is AOT- and
/// trim-safe: callers pass <c>MyJsonContext.Default.MyMessage</c> rather than relying on reflection.
/// </remarks>
public static class JsonChannelSerialization
{
    /// <summary>
    /// Creates a JSON serializer that writes a message directly into the channel's buffer.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="typeInfo">The source-generated type metadata for <typeparamref name="TMessage"/>.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<TMessage> CreateSerializer<TMessage>(JsonTypeInfo<TMessage> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return (message, output) =>
        {
            using var jsonWriter = new Utf8JsonWriter(output);
            JsonSerializer.Serialize(jsonWriter, message, typeInfo);
        };
    }


    /// <summary>
    /// Creates a JSON deserializer that reads a message from a framed payload.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="typeInfo">The source-generated type metadata for <typeparamref name="TMessage"/>.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<TMessage> CreateDeserializer<TMessage>(JsonTypeInfo<TMessage> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return payload =>
        {
            var jsonReader = new Utf8JsonReader(payload);

            TMessage? message = JsonSerializer.Deserialize(ref jsonReader, typeInfo);

            //A channel message is never null. A payload that is the JSON literal "null" deserializes to a
            //null reference, which the null-forgiving operator would otherwise smuggle through; reject it
            //explicitly so the channel never yields a null message.
            if(message is null)
            {
                throw new JsonException("the payload is the JSON literal null, which is not a message");
            }

            //Reject trailing data after the JSON value. Deserialize stops at the end of the first value, so
            //anything that remains (beyond insignificant whitespace, which Utf8JsonReader skips) is a second
            //token. Allowing it would let multiple distinct byte sequences decode to the same message, which
            //breaks canonical-bytes assumptions if these codecs are ever reused near digest-relevant content.
            if(jsonReader.Read())
            {
                throw new JsonException("the payload contains trailing data after the JSON value");
            }

            return message;
        };
    }
}
