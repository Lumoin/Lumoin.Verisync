namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads one value back from a format's read side at its current position.
/// </summary>
/// <typeparam name="TSource">The format's read side: an element of a parsed document, or a positioned
/// reader.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="source">The element or reader holding exactly what the matching
/// <see cref="WriteValueDelegate{TWriter, TValue}"/> wrote.</param>
/// <returns>The value read.</returns>
/// <remarks>
/// An implementation re-runs the value's own domain validation and throws on a payload it cannot accept; a
/// codec factory runs it inside its fail-closed guard, so that throw reaches a channel consumer as
/// <see cref="MessageDeserializationException"/> like every other malformed payload. With a reader-backed
/// source it consumes exactly the value's own encoding, because the channel refuses a payload carrying data
/// after the message.
/// </remarks>
public delegate TValue ReadValueDelegate<in TSource, out TValue>(TSource source);
