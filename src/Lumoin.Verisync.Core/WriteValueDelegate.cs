namespace Lumoin.Verisync.Core;

/// <summary>
/// Writes one value's encoding at a format writer's current position.
/// </summary>
/// <typeparam name="TWriter">The format's writer type, such as a JSON or CBOR writer.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="writer">The writer, positioned where the value belongs.</param>
/// <param name="value">The value to write.</param>
/// <remarks>
/// One shape serves every encoding the codecs speak, the way <see cref="SerializeMessageDelegate{TMessage}"/>
/// does one layer up: the format enters only through <typeparamref name="TWriter"/>. An implementation
/// writes one complete value and nothing around it — the codec that invoked it owns whatever envelope or
/// framing surrounds the slot — and what it writes must be what the matching
/// <see cref="ReadValueDelegate{TSource, TValue}"/> reads back.
/// </remarks>
public delegate void WriteValueDelegate<in TWriter, in TValue>(TWriter writer, TValue value);
