namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of an <see cref="OrSet{T}"/>. Obtain it with <see cref="OrSet{T}.ToState"/> and
/// reconstruct with <see cref="OrSet{T}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Set">The serialized underlying dotted-version-vector set.</param>
public sealed record OrSetState<TValue>(DottedVersionVectorSetState<TValue> Set);
