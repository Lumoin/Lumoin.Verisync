using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Deserializes a message from the payload bytes of a single framed message. The format is the caller's
/// choice, matching the <see cref="SerializeMessageDelegate{TMessage}"/> used to write it.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <param name="payload">The complete payload bytes of one framed message.</param>
/// <returns>The deserialized message.</returns>
public delegate TMessage DeserializeMessageDelegate<out TMessage>(ReadOnlySequence<byte> payload);
