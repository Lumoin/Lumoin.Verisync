using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads one value from its bytes, in no particular encoding.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="payload">The value's bytes, exactly as <see cref="EncodeValueDelegate{TValue}"/> wrote them.</param>
/// <returns>The value.</returns>
/// <remarks>
/// <para>
/// The reading half of the format-neutral value seam, and the counterpart of
/// <see cref="ReadValueDelegate{TSource, TValue}"/> as <see cref="EncodeValueDelegate{TValue}"/> is of
/// <see cref="WriteValueDelegate{TWriter, TValue}"/>.
/// </para>
/// <para>
/// It takes a span rather than the sequence <see cref="DeserializeMessageDelegate{TMessage}"/> takes, and the
/// difference is not an oversight. A message arrives from a transport that may fragment it; a value is a slot
/// the codec has already located inside one message, so it is contiguous by construction. A sequence here
/// would invite a copy at every call to make it contiguous again, and the shape that cannot express the copy
/// is the one that never pays for it.
/// </para>
/// </remarks>
public delegate TValue DecodeValueDelegate<out TValue>(ReadOnlySpan<byte> payload);
