using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="LwwRegister{TValue}"/>: its value, write timestamp, and writer.
/// Obtain it with <see cref="LwwRegister{TValue}.ToState"/> and reconstruct with
/// <see cref="LwwRegister{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="HasValue">Whether the register held a value.</param>
/// <param name="Value">The stored value, or the default value when the register was empty.</param>
/// <param name="UtcTicks">The write timestamp in UTC ticks, or zero when the register was empty.</param>
/// <param name="Writer">The writing replica's raw identifier bytes, or empty when the register was empty.</param>
public sealed record LwwRegisterState<TValue>(bool HasValue, TValue? Value, long UtcTicks, ImmutableArray<byte> Writer);
