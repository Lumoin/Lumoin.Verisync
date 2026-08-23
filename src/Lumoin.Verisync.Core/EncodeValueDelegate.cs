using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Writes one value's bytes, in no particular encoding.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="value">The value to write.</param>
/// <param name="output">The sink the value's bytes are written to.</param>
/// <remarks>
/// <para>
/// This is the format-neutral half of the value seam, and it is the one most callers want.
/// <see cref="WriteValueDelegate{TWriter, TValue}"/> is typed on the format's own writer, which buys native
/// shaping — a value that appears in JSON as an object rather than as a string — at two costs: the caller
/// names a serialization type in its own code, and one value type needs one codec per format because the two
/// delegate instantiations are unrelated types. A value written through this delegate is carried by whatever
/// byte container the format has, so one implementation serves every codec.
/// </para>
/// <para>
/// It takes the same shape as <see cref="SerializeMessageDelegate{TMessage}"/> one layer up, for the same
/// reason: a sink the caller writes into rather than a buffer it must allocate and return.
/// </para>
/// <para>
/// An implementation writes one complete value and nothing around it, and what it writes must be what the
/// matching <see cref="DecodeValueDelegate{TValue}"/> reads back.
/// </para>
/// </remarks>
public delegate void EncodeValueDelegate<in TValue>(TValue value, IBufferWriter<byte> output);
