namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="MvRegister{TValue}"/>. Obtain it with
/// <see cref="MvRegister{TValue}.ToState"/> and reconstruct with <see cref="MvRegister{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Entries">The serialized underlying dotted-version-vector set.</param>
public sealed record MvRegisterState<TValue>(DottedVersionVectorSetState<TValue> Entries);
