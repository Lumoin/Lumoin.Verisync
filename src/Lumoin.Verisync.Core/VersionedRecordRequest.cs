using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A <see cref="RecordRequest{TValue}"/> addressed to one consensus instance of a versioned register, which
/// is the register version the write would produce.
/// </summary>
/// <typeparam name="TValue">The consensus value type, which a versioned register instantiates at <see cref="VersionedValue{TValue}"/>.</typeparam>
/// <param name="Version">The register version this request's instance produces. Must not be <see cref="RegisterVersion.Unwritten"/>.</param>
/// <param name="Request">The request itself, unchanged. Must not be <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// The version is a guard rather than a route. A recorder host serves one instance and already knows which,
/// so nothing here selects a destination; the field makes a request naming a different instance refusable.
/// Without it a stale proposer's request would be indistinguishable from a live one and would be folded into
/// the wrong register.
/// </para>
/// <para>
/// It is not a discriminator, and the <see cref="RecordRequest{TValue}"/> family has none. A channel carrying
/// this type is monotyped exactly as one carrying the bare request is, and nothing decodes a payload to learn
/// which of several message kinds it holds.
/// </para>
/// <para>
/// The envelope wraps rather than extends, so the inner message keeps its field set and its byte-for-byte
/// encoding.
/// </para>
/// <para>
/// A reply's version must match its request's, and unlike the step it is checkable above the transport,
/// because the caller holds the version it sent. The step is not checkable there, because a reply carries the
/// recorder's own step rather than the step of the request it answers.
/// </para>
/// </remarks>
public sealed record VersionedRecordRequest<TValue>(RegisterVersion Version, RecordRequest<TValue> Request)
{
    /// <summary>
    /// The register version this request's instance produces. It is validated on construction and on a
    /// <c>with</c> expression alike, because the initializer writes the backing field directly and no accessor
    /// runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion Version { get; init { field = ValidateVersion(value); } } = ValidateVersion(Version);


    /// <summary>
    /// The request itself. It is validated on construction and on a <c>with</c> expression alike, for the same
    /// reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the request is <see langword="null"/>.</exception>
    public RecordRequest<TValue> Request { get; init { field = ValidateRequest(value); } } = ValidateRequest(Request);


    private static RegisterVersion ValidateVersion(RegisterVersion value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(Version));

        return value;
    }


    private static RecordRequest<TValue> ValidateRequest(RecordRequest<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Request));

        return value;
    }
}
