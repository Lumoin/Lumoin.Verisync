using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Deserializes a message from the payload bytes of a single framed message. The format is the caller's
/// choice, matching the <see cref="SerializeMessageDelegate{TMessage}"/> used to write it.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <param name="payload">The complete payload bytes of one framed message.</param>
/// <returns>The deserialized message.</returns>
/// <exception cref="MessageDeserializationException">
/// Thrown when the payload cannot be deserialized into a valid message — a malformed encoding, a missing or
/// rejected field, or a failed verification. This is the uniform failure across every encoding; the
/// encoding-specific cause is carried as the inner exception. Implementations built by the Verisync JSON and
/// CBOR codecs honour this contract.
/// </exception>
public delegate TMessage DeserializeMessageDelegate<out TMessage>(ReadOnlySequence<byte> payload);
