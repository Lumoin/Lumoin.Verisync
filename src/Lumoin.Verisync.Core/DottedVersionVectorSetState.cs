using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="DottedVersionVectorSet{T}"/>: its causal context and dotted entries.
/// Obtain it with <see cref="DottedVersionVectorSet{T}.ToState"/> and reconstruct with
/// <see cref="DottedVersionVectorSet{T}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Entries">The serialized dotted entries.</param>
public sealed record DottedVersionVectorSetState<TValue>(VectorClockState Context, ImmutableArray<DottedEntry<TValue>> Entries);
