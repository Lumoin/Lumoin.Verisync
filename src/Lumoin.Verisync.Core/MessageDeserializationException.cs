using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Thrown when a payload cannot be deserialized into a valid message — a malformed encoding, a missing or
/// rejected field, or a failed verification. It is the single failure a
/// <see cref="DeserializeMessageDelegate{TMessage}"/> raises regardless of the wire encoding (JSON, CBOR, or
/// another), so a channel consumer catches one type rather than an encoding-specific exception. The
/// encoding-specific cause — a <see cref="System.Text.Json.JsonException"/>, a
/// <see cref="System.Formats.Cbor.CborContentException"/>, a wrapped argument exception, and so on — is
/// preserved as <see cref="Exception.InnerException"/> for diagnostics.
/// </summary>
public sealed class MessageDeserializationException: Exception
{
    /// <summary>Initializes a new instance.</summary>
    public MessageDeserializationException()
    {
    }


    /// <summary>Initializes a new instance with an error message.</summary>
    /// <param name="message">The message that describes the deserialization failure.</param>
    public MessageDeserializationException(string message): base(message)
    {
    }


    /// <summary>Initializes a new instance with an error message and the encoding-specific cause.</summary>
    /// <param name="message">The message that describes the deserialization failure.</param>
    /// <param name="innerException">The encoding-specific exception that caused this failure.</param>
    public MessageDeserializationException(string message, Exception innerException): base(message, innerException)
    {
    }
}
