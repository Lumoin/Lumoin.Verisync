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

            return JsonSerializer.Deserialize(ref jsonReader, typeInfo)!;
        };
    }
}
