using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Serializes a message into a buffer. The format is the caller's choice — JSON, CBOR, or anything else —
/// so the transport seam stays serialization-agnostic.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <param name="message">The message to serialize.</param>
/// <param name="output">
/// The buffer to write the serialized payload into. A <see cref="System.IO.Pipelines.PipeWriter"/> is an
/// <see cref="IBufferWriter{T}"/>, so an implementation may write straight into the channel.
/// </param>
public delegate void SerializeMessageDelegate<in TMessage>(TMessage message, IBufferWriter<byte> output);
