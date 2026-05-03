using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One entry of a serialized dotted-version-vector set: a dot (replica bytes and counter) paired with its value.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Replica">The dot replica's raw identifier bytes.</param>
/// <param name="Counter">The dot's counter.</param>
/// <param name="Value">The value the dot tags.</param>
public sealed record DottedEntry<TValue>(ImmutableArray<byte> Replica, int Counter, TValue Value);
